using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Blockchain.Core.Contracts;
using Blockchain.Core.Constants;

namespace Blockchain.Core
{
    public class BlockchainManager
    {
        private readonly DatabaseManager _db;
        private readonly ContractExecutor _executor;
        private static readonly DateTime GenesisTimestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public BlockchainManager(string dbFileName, string dbPassword)
        {
            _db = new DatabaseManager(dbFileName, dbPassword);
            _executor = new ContractExecutor();

            var currentChain = _db.LoadChain("System");
            if (currentChain.Count == 0)
            {
                AddGenesisBlock();
            }
            else if (!IsValidChain("System"))
            {
                Console.WriteLine("[BlockchainManager] Blockchain integrity check failed!");
            }
        }

        public Block? GetLatestBlock(string channelId = "System")
        {
            var channelChain = _db.LoadChain(channelId);
            return channelChain.LastOrDefault();
        }

        public void AddGenesisBlock()
        {
            var genesisBlock = new Block
            {
                Index = 0,
                PreviousHash = "0",
                Data = "{\"Source\":\"System\",\"Message\":\"Nexus Genesis Block\"}",
                Timestamp = GenesisTimestamp,
                ChannelId = "System"
            };
            MineBlock(genesisBlock);
            _db.SaveBlock(genesisBlock);
        }

        public bool AddBlock(Block newBlock)
        {
            var latestBlock = GetLatestBlock(newBlock.ChannelId);

            int expectedIndex = latestBlock != null ? latestBlock.Index + 1 : 0;
            string expectedPrevHash = latestBlock != null ? latestBlock.Hash : "0";

            if (newBlock.Index != expectedIndex)
            {
                Console.WriteLine($"[Blockchain] Invalid Index. Expected {expectedIndex}, got {newBlock.Index}");
                return false;
            }

            if (newBlock.PreviousHash != expectedPrevHash)
            {
                Console.WriteLine("[Blockchain] Invalid PreviousHash.");
                return false;
            }

            if (!newBlock.VerifySignature())
            {
                Console.WriteLine("[Blockchain] Block signature verification failed!");
                return false;
            }

            MineBlock(newBlock);
            _db.SaveBlock(newBlock, newBlock.ChannelId);
            return true;
        }

        public bool ProcessPeerBlock(Block peerBlock)
        {
            peerBlock.ChannelId = string.IsNullOrWhiteSpace(peerBlock.ChannelId) ? "System" : peerBlock.ChannelId;

            if (!IsPayloadChannelConsistent(peerBlock.Data, peerBlock.ChannelId))
            {
                Console.WriteLine($"[Blockchain] Rejected block {peerBlock.Hash}: payload/channel mismatch.");
                return false;
            }

            if (_db.BlockExists(peerBlock.Hash, peerBlock.ChannelId))
            {
                return true;
            }

            var latestBlock = GetLatestBlock(peerBlock.ChannelId);

            int expectedIndex = latestBlock != null ? latestBlock.Index + 1 : 0;
            string expectedPrevHash = latestBlock != null ? latestBlock.Hash : "0";

            if (peerBlock.Index != expectedIndex || peerBlock.PreviousHash != expectedPrevHash)
            {
                Console.WriteLine($"[Blockchain] Channel '{peerBlock.ChannelId}' rejection: Index or Hash mismatch.");
                _db.SavePendingBlock(peerBlock, "Index or PreviousHash mismatch");
                return false;
            }

            if (!peerBlock.VerifySignature()) return false;
            if (peerBlock.Hash != peerBlock.CalculateHash()) return false;
            if (!_executor.Execute(peerBlock.Data, peerBlock.ValidatorPublicKey, _db)) return false;
            if (!peerBlock.Hash.StartsWith(NetworkParameters.TargetPrefix)) return false;

            _db.SaveBlock(peerBlock, peerBlock.ChannelId);
            TryConnectPendingBlocks(peerBlock.ChannelId, peerBlock.Hash);
            return true;
        }

        private static bool IsPayloadChannelConsistent(string data, string channelId)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return true;
            }

            string trimmed = data.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return true;
                }

                var root = doc.RootElement;
                string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                string projectId = root.TryGetProperty("ProjectId", out var projectProp) ? projectProp.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(type))
                {
                    return true;
                }

                string safeChannel = DatabaseManager.SanitizeChannelName(channelId);
                string safeProject = DatabaseManager.SanitizeChannelName(projectId);

                bool isSystemOnlyEvent = type == "CreateProject" || type == "AssignRole";
                if (isSystemOnlyEvent)
                {
                    return safeChannel == "System";
                }

                bool isProjectScopedEvent =
                    type == "Create" || type == "Update" || type == "Move" ||
                    type == "CreateDocument" || type == "UpdateDocument" ||
                    type == "CreateProposal" || type == "CastVote" ||
                    type == "CodeCommit" || type == "Register" || type == "Transfer";

                if (!isProjectScopedEvent)
                {
                    return true;
                }

                if (string.IsNullOrWhiteSpace(projectId))
                {
                    return false;
                }

                if (safeChannel == "System")
                {
                    return false;
                }

                return safeProject == safeChannel;
            }
            catch
            {
                return false;
            }
        }

        public bool TryAdoptChain(string channelId, List<Block> candidateChain)
        {
            string safeChannel = DatabaseManager.SanitizeChannelName(channelId);
            var orderedCandidate = candidateChain
                .OrderBy(block => block.Index)
                .Select(block =>
                {
                    block.ChannelId = string.IsNullOrWhiteSpace(block.ChannelId) ? safeChannel : block.ChannelId;
                    return block;
                })
                .ToList();

            var currentChain = _db.LoadChain(safeChannel);
            if (orderedCandidate.Count <= currentChain.Count)
            {
                return false;
            }

            if (!IsStructurallyValidChain(orderedCandidate, safeChannel))
            {
                Console.WriteLine($"[Blockchain] Rejected candidate chain for '{safeChannel}': invalid structure.");
                return false;
            }

            if (!CanReplayCandidateAgainstContracts(safeChannel, orderedCandidate))
            {
                Console.WriteLine($"[Blockchain] Rejected candidate chain for '{safeChannel}': contract/state replay failed.");
                return false;
            }

            _db.ReplaceChain(safeChannel, orderedCandidate);
            Console.WriteLine($"[Blockchain] Adopted validated longer chain for '{safeChannel}'. Height: {orderedCandidate.Count - 1}");
            return true;
        }

        public void MineBlock(Block block)
        {
            do
            {
                block.Nonce++;
                block.Hash = block.CalculateHash();
            }
            while (!block.Hash.StartsWith(NetworkParameters.TargetPrefix));
        }

        public bool IsValidChain(string channelId = "System")
        {
            var chain = _db.LoadChain(channelId);
            for (int i = 1; i < chain.Count; i++)
            {
                var currentBlock = chain[i];
                var previousBlock = chain[i - 1];

                if (currentBlock.Hash != currentBlock.CalculateHash()) return false;
                if (currentBlock.PreviousHash != previousBlock.Hash) return false;
                if (!currentBlock.Hash.StartsWith(NetworkParameters.TargetPrefix)) return false;
                if (!currentBlock.VerifySignature()) return false;
            }
            return true;
        }

        private void TryConnectPendingBlocks(string channelId, string parentHash)
        {
            foreach (var pending in _db.LoadPendingChildren(parentHash, channelId))
            {
                if (ProcessPeerBlock(pending))
                {
                    _db.RemovePendingBlock(pending.Hash);
                }
            }
        }

        private bool IsStructurallyValidChain(List<Block> chain, string channelId)
        {
            if (chain.Count == 0) return false;

            for (int i = 0; i < chain.Count; i++)
            {
                var current = chain[i];
                current.ChannelId = string.IsNullOrWhiteSpace(current.ChannelId) ? channelId : current.ChannelId;

                if (!IsPayloadChannelConsistent(current.Data, current.ChannelId))
                {
                    return false;
                }

                if (current.Index != i) return false;
                if (current.Hash != current.CalculateHash()) return false;
                if (!current.Hash.StartsWith(NetworkParameters.TargetPrefix)) return false;
                if (!current.VerifySignature()) return false;

                if (i == 0)
                {
                    if (current.PreviousHash != "0") return false;
                    if (channelId == "System" && !current.IsSystemGenesisBlock()) return false;
                }
                else if (current.PreviousHash != chain[i - 1].Hash)
                {
                    return false;
                }
            }

            return true;
        }

        private bool CanReplayCandidateAgainstContracts(string targetChannel, List<Block> targetCandidateChain)
        {
            string tmpDb = Path.Combine(Path.GetTempPath(), $"nexus_adopt_{Guid.NewGuid():N}.db");
            try
            {
                var replayDb = new DatabaseManager(tmpDb, "");

                var knownChannels = _db.GetKnownChannels()
                    .Select(DatabaseManager.SanitizeChannelName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!knownChannels.Contains("System", StringComparer.OrdinalIgnoreCase))
                {
                    knownChannels.Insert(0, "System");
                }

                var channelsToReplay = new List<string> { "System" };
                channelsToReplay.AddRange(
                    knownChannels
                        .Where(channel => !string.Equals(channel, "System", StringComparison.OrdinalIgnoreCase) &&
                                          !string.Equals(channel, targetChannel, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(channel => channel, StringComparer.OrdinalIgnoreCase));

                if (!string.Equals(targetChannel, "System", StringComparison.OrdinalIgnoreCase))
                {
                    channelsToReplay.Add(targetChannel);
                }

                foreach (string channel in channelsToReplay)
                {
                    List<Block> chainToReplay;
                    if (string.Equals(channel, targetChannel, StringComparison.OrdinalIgnoreCase))
                    {
                        chainToReplay = targetCandidateChain;
                    }
                    else
                    {
                        chainToReplay = _db.LoadChain(channel)
                            .OrderBy(block => block.Index)
                            .ToList();
                    }

                    if (!ReplayChain(channel, chainToReplay, replayDb))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Blockchain] Candidate replay failed: {ex.Message}");
                return false;
            }
            finally
            {
                TryDelete(tmpDb);
                TryDelete(tmpDb + "-wal");
                TryDelete(tmpDb + "-shm");
            }
        }

        private bool ReplayChain(string channelId, List<Block> chain, DatabaseManager replayDb)
        {
            if (chain.Count == 0)
            {
                return channelId != "System";
            }

            for (int i = 0; i < chain.Count; i++)
            {
                var current = chain[i];
                string normalizedChannel = DatabaseManager.SanitizeChannelName(
                    string.IsNullOrWhiteSpace(current.ChannelId) ? channelId : current.ChannelId);

                if (!string.Equals(normalizedChannel, channelId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!IsPayloadChannelConsistent(current.Data, normalizedChannel))
                {
                    return false;
                }

                if (current.Index != i) return false;
                if (current.Hash != current.CalculateHash()) return false;
                if (!current.Hash.StartsWith(NetworkParameters.TargetPrefix)) return false;
                if (!current.VerifySignature()) return false;

                if (i == 0)
                {
                    if (current.PreviousHash != "0") return false;
                    if (channelId == "System" && !current.IsSystemGenesisBlock()) return false;
                }
                else if (current.PreviousHash != chain[i - 1].Hash)
                {
                    return false;
                }

                if (!_executor.Execute(current.Data, current.ValidatorPublicKey, replayDb))
                {
                    return false;
                }

                replayDb.SaveBlock(current, normalizedChannel);
            }

            return true;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}
