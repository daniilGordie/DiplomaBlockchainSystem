using Blockchain.Core.Constants;
using Blockchain.Core.Contracts;

namespace Blockchain.Core;

public sealed class ChainAdoptionService
{
    private readonly IChainReader _chainReader;
    private readonly IChainWriter _chainWriter;
    private readonly IReplayStoreFactory _replayStoreFactory;
    private readonly ContractExecutor _executor;

    public ChainAdoptionService(
        IChainReader chainReader,
        IChainWriter chainWriter,
        IReplayStoreFactory replayStoreFactory)
    {
        _chainReader = chainReader;
        _chainWriter = chainWriter;
        _replayStoreFactory = replayStoreFactory;
        _executor = new ContractExecutor();
    }

    public bool TryAdoptChain(string channelId, List<Block> candidateChain)
    {
        string safeChannel = ChannelName.Normalize(channelId);
        var orderedCandidate = candidateChain
            .OrderBy(block => block.Index)
            .Select(block =>
            {
                block.ChannelId = string.IsNullOrWhiteSpace(block.ChannelId) ? safeChannel : block.ChannelId;
                return block;
            })
            .ToList();

        var currentChain = _chainReader.LoadChain(safeChannel);
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

        _chainWriter.ReplaceChain(safeChannel, orderedCandidate);
        Console.WriteLine($"[Blockchain] Adopted validated longer chain for '{safeChannel}'. Height: {orderedCandidate.Count - 1}");
        return true;
    }

    private static bool IsStructurallyValidChain(List<Block> chain, string channelId)
    {
        if (chain.Count == 0) return false;

        for (int i = 0; i < chain.Count; i++)
        {
            var current = chain[i];
            current.ChannelId = string.IsNullOrWhiteSpace(current.ChannelId) ? channelId : current.ChannelId;

            if (!BlockPayloadChannelPolicy.IsConsistent(current.Data, current.ChannelId))
            {
                return false;
            }

            if (current.Index != i) return false;
            if (current.Hash != current.CalculateHash()) return false;
            if (NetworkParameters.RequireProofOfWork && !current.Hash.StartsWith(NetworkParameters.TargetPrefix)) return false;
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
        IBlockchainStore? replayStore = null;
        try
        {
            replayStore = _replayStoreFactory.CreateReplayStore();

            var knownChannels = _chainReader.GetKnownChannels()
                .Select(ChannelName.Normalize)
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
                List<Block> chainToReplay = string.Equals(channel, targetChannel, StringComparison.OrdinalIgnoreCase)
                    ? targetCandidateChain
                    : _chainReader.LoadChain(channel)
                        .OrderBy(block => block.Index)
                        .ToList();

                if (!ReplayChain(channel, chainToReplay, replayStore))
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
            if (replayStore != null)
            {
                _replayStoreFactory.CleanupReplayStore(replayStore);
            }
        }
    }

    private bool ReplayChain(string channelId, List<Block> chain, IBlockchainStore replayStore)
    {
        if (chain.Count == 0)
        {
            return channelId != "System";
        }

        for (int i = 0; i < chain.Count; i++)
        {
            var current = chain[i];
            string normalizedChannel = ChannelName.Normalize(
                string.IsNullOrWhiteSpace(current.ChannelId) ? channelId : current.ChannelId);

            if (!string.Equals(normalizedChannel, channelId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!BlockPayloadChannelPolicy.IsConsistent(current.Data, normalizedChannel))
            {
                return false;
            }

            if (current.Index != i) return false;
            if (current.Hash != current.CalculateHash()) return false;
            if (NetworkParameters.RequireProofOfWork && !current.Hash.StartsWith(NetworkParameters.TargetPrefix)) return false;
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

            if (!_executor.Execute(current.Data, current.ValidatorPublicKey, replayStore))
            {
                return false;
            }

            replayStore.SaveBlock(current, normalizedChannel);
        }

        return true;
    }
}
