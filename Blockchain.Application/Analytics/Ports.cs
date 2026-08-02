namespace Blockchain.Application.Analytics;

public interface IProjectAnalyticsReader
{
    IReadOnlyList<BlockSnapshot> LoadChain(string projectId);
    ProjectAnalyticsState LoadState(string projectId);
}

public interface IBlockAuditVerifier
{
    bool HasValidHash(BlockSnapshot block);
    bool HasValidSignature(BlockSnapshot block);
}
