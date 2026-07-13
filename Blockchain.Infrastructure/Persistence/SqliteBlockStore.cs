using Blockchain.Core;
using Blockchain.Core.Consensus;

namespace Blockchain.Infrastructure.Persistence;

public sealed class SqliteBlockStore : IBlockchainStore
{
    private readonly DatabaseManager _database;

    public SqliteBlockStore(DatabaseManager database)
    {
        _database = database;
    }

    public bool HasBlocks(string channelId = "System") => _database.HasBlocks(channelId);
    public Block? GetLatestBlock(string channelId = "System") => _database.GetLatestBlock(channelId);
    public bool BlockExists(string blockHash, string channelId = "System") => _database.BlockExists(blockHash, channelId);
    public List<string> GetKnownChannels() => _database.GetKnownChannels();
    public List<Block> LoadChain(string channelId = "System") => _database.LoadChain(channelId);
    public List<Block> LoadLatestBlocks(string channelId = "System", int count = 100) => _database.LoadLatestBlocks(channelId, count);
    public void SaveBlock(Block block, string channelId = "System") => _database.SaveBlock(block, channelId);
    public void ReplaceChain(string channelId, List<Block> blocks) => _database.ReplaceChain(channelId, blocks);
    public void SavePendingBlock(Block block, string reason) => _database.SavePendingBlock(block, reason);
    public List<Block> LoadPendingChildren(string previousHash, string channelId) => _database.LoadPendingChildren(previousHash, channelId);
    public void RemovePendingBlock(string blockHash) => _database.RemovePendingBlock(blockHash);
    public void SaveFinalityMetadata(BlockFinalityMetadata metadata) => _database.SaveFinalityMetadata(metadata);
    public BlockFinalityMetadata? GetFinalityMetadata(string blockHash) => _database.GetFinalityMetadata(blockHash);
    public bool HasFinalityMetadata(string blockHash) => _database.HasFinalityMetadata(blockHash);
    public void SaveContributionProof(string blockHash, ContributionProof proof) => _database.SaveContributionProof(blockHash, proof);
    public ContributionProof? GetContributionProof(string blockHash) => _database.GetContributionProof(blockHash);
    public void SaveIntent(IntentOutboxRecord record) => _database.SaveIntent(record);
    public IntentOutboxRecord? GetIntent(string intentId) => _database.GetIntent(intentId);
    public List<IntentOutboxRecord> LoadRetryableIntents(DateTime nowUtc, int limit) => _database.LoadRetryableIntents(nowUtc, limit);
    public List<IntentOutboxRecord> LoadRecentIntents(int limit) => _database.LoadRecentIntents(limit);
    public List<IntentStatusTransition> LoadIntentHistory(string intentId) => _database.LoadIntentHistory(intentId);
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
        string? proposalId = null) =>
        _database.UpdateIntentStatus(intentId, status, attemptCount, updatedAtUtc, lastAttemptAtUtc, nextAttemptAtUtc, lastError, destination, committedBlockHash, committedBlockIndex, proposalId);
    public List<string> GetUserProjects(string userName) => _database.GetUserProjects(userName);
    public string? GetUserPublicKey(string userName) => _database.GetUserPublicKey(userName);
    public string GetUserRole(string projectId, string userName) => _database.GetUserRole(projectId, userName);
    public int GetUserBalance(string userName) => _database.GetUserBalance(userName);
    public bool IsProjectExists(string projectId) => _database.IsProjectExists(projectId);
    public string? GetProposalCreator(string proposalId) => _database.GetProposalCreator(proposalId);
}
