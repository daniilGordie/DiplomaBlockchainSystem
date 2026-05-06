using System;
using System.Collections.Generic;
using System.Linq;
using Blockchain.Core.Contracts;
using Blockchain.Core.Constants;

namespace Blockchain.Core
{
    public class BlockchainManager
    {
        private readonly DatabaseManager _db;
        private readonly ContractExecutor _executor;

        public BlockchainManager(string dbFileName = "nexus_node.db")
        {
            _db = new DatabaseManager(dbFileName);
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

        public Block GetLatestBlock(string channelId = "System")
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
                Timestamp = DateTime.UtcNow
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
            var latestBlock = GetLatestBlock(peerBlock.ChannelId);

            int expectedIndex = latestBlock != null ? latestBlock.Index + 1 : 0;
            string expectedPrevHash = latestBlock != null ? latestBlock.Hash : "0";

            if (peerBlock.Index != expectedIndex || peerBlock.PreviousHash != expectedPrevHash)
            {
                Console.WriteLine($"[Blockchain] Channel '{peerBlock.ChannelId}' rejection: Index or Hash mismatch.");
                return false;
            }

            if (!peerBlock.VerifySignature()) return false;
            if (!_executor.Execute(peerBlock.Data, peerBlock.ValidatorPublicKey, _db)) return false;
            if (!peerBlock.Hash.StartsWith(NetworkParameters.TargetPrefix)) return false;

            _db.SaveBlock(peerBlock, peerBlock.ChannelId);
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
    }
}