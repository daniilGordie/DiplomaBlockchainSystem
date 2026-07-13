using Blockchain.Core;
using Blockchain.Core.Consensus;

public class BlockchainManagerPortTests
{
    [Fact]
    public void Constructor_ShouldUseBlockchainStorePort()
    {
        var store = new InMemoryBlockchainStore();

        var manager = new BlockchainManager(store);

        var genesis = manager.GetLatestBlock("System");
        Assert.NotNull(genesis);
        Assert.Equal("System", genesis.ChannelId);
        Assert.True(store.HasBlocks("System"));
    }

    private sealed class InMemoryBlockchainStore : IBlockchainStore
    {
        private readonly Dictionary<string, List<Block>> _chains = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ContributionProof> _proofs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IntentOutboxRecord> _intents = new(StringComparer.OrdinalIgnoreCase);

        public bool HasBlocks(string channelId = "System") => LoadChain(channelId).Count > 0;

        public Block? GetLatestBlock(string channelId = "System") => LoadChain(channelId).LastOrDefault();

        public void SaveBlock(Block block, string channelId = "System")
        {
            var chain = LoadChain(channelId);
            block.ChannelId = channelId;
            chain.Add(block);
        }

        public bool BlockExists(string blockHash, string channelId = "System") =>
            LoadChain(channelId).Any(block => block.Hash == blockHash);

        public void SavePendingBlock(Block block, string reason)
        {
        }

        public List<Block> LoadPendingChildren(string previousHash, string channelId) => new();

        public void RemovePendingBlock(string blockHash)
        {
        }

        public void SaveFinalityMetadata(BlockFinalityMetadata metadata)
        {
        }

        public BlockFinalityMetadata? GetFinalityMetadata(string blockHash) => null;

        public bool HasFinalityMetadata(string blockHash) => false;

        public void SaveContributionProof(string blockHash, ContributionProof proof)
        {
            _proofs[blockHash] = proof;
        }

        public ContributionProof? GetContributionProof(string blockHash) =>
            _proofs.TryGetValue(blockHash, out var proof) ? proof : null;

        public List<string> GetKnownChannels() => _chains.Keys.ToList();

        public List<Block> LoadChain(string channelId = "System")
        {
            if (!_chains.TryGetValue(channelId, out var chain))
            {
                chain = new List<Block>();
                _chains[channelId] = chain;
            }

            return chain;
        }

        public List<Block> LoadLatestBlocks(string channelId = "System", int count = 100) =>
            LoadChain(channelId)
                .OrderByDescending(block => block.Index)
                .Take(count)
                .ToList();

        public void ReplaceChain(string channelId, List<Block> blocks)
        {
            _chains[channelId] = blocks;
        }

        public void SaveIntent(IntentOutboxRecord record)
        {
            _intents[record.Intent.IntentId] = record;
        }

        public IntentOutboxRecord? GetIntent(string intentId) =>
            _intents.TryGetValue(intentId, out var record) ? record : null;

        public List<IntentOutboxRecord> LoadRetryableIntents(DateTime nowUtc, int limit) =>
            _intents.Values.Take(limit).ToList();

        public List<IntentOutboxRecord> LoadRecentIntents(int limit) =>
            _intents.Values.Take(limit).ToList();

        public List<IntentStatusTransition> LoadIntentHistory(string intentId) => new();

        public void UpdateIntentStatus(
            string intentId,
            IntentStatus status,
            int attemptCount,
            DateTime updatedAtUtc,
            DateTime? lastAttemptAtUtc,
            DateTime? nextAttemptAtUtc,
            string lastError,
            string destination,
            string? committedBlockHash,
            long? committedBlockIndex = null,
            string? proposalId = null)
        {
            if (!_intents.TryGetValue(intentId, out var record))
            {
                return;
            }

            _intents[intentId] = record with
            {
                Status = status,
                AttemptCount = attemptCount,
                UpdatedAtUtc = updatedAtUtc,
                LastAttemptAtUtc = lastAttemptAtUtc,
                NextAttemptAtUtc = nextAttemptAtUtc,
                LastError = lastError,
                Destination = destination,
                CommittedBlockHash = committedBlockHash,
                CommittedBlockIndex = committedBlockIndex,
                ProposalId = proposalId
            };
        }

        public List<string> GetUserProjects(string userName) => new();

        public string? GetUserPublicKey(string userName) => null;
        public string GetUserRole(string projectId, string userName) => "Owner";
        public int GetUserBalance(string userName) => 0;
        public bool IsProjectExists(string projectId) => true;
        public string? GetProposalCreator(string proposalId) => null;
    }
}
