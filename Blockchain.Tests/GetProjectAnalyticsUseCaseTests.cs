using Blockchain.Application.Analytics;

namespace Blockchain.Tests;

public class GetProjectAnalyticsUseCaseTests
{
    [Fact]
    public void Execute_ShouldAggregateProjectStatePayloadsAndTiming()
    {
        var blocks = new[]
        {
            CreateBlock(0, "hash-0", "0", "{\"Type\":\"CodeCommit\",\"CommitHash\":\"abc\"}"),
            CreateBlock(1, "hash-1", "hash-0", "{\"Type\":\"CodeCommit\",\"CommitHash\":\"ABC\",\"FileHash\":\"file-1\"}"),
            CreateBlock(2, "hash-2", "hash-1", "{\"FileHash\":\"file-2\"}"),
            CreateBlock(3, "hash-3", "hash-2", "not-json")
        };
        var state = new ProjectAnalyticsState(
            TotalTasks: 7,
            TotalDocuments: 3,
            TasksTodo: 2,
            TasksInProgress: 4,
            TasksDone: 1,
            UserReputation: new Dictionary<string, int> { ["Alice"] = 15 });
        var useCase = new GetProjectAnalyticsUseCase(
            new FakeAnalyticsReader(blocks, state),
            new AlwaysValidBlockAuditVerifier());

        var result = useCase.Execute("ProjectA");

        Assert.Equal(4, result.TotalBlocks);
        Assert.Equal(7, result.TotalTasks);
        Assert.Equal(1, result.TotalCommits);
        Assert.Equal(2, result.TotalArtifacts);
        Assert.Equal(2, result.TasksTodo);
        Assert.Equal(4, result.TasksInProgress);
        Assert.Equal(1, result.TasksDone);
        Assert.Equal(3, result.TotalDocuments);
        Assert.Equal(15, result.UserReputation["Alice"]);
        Assert.Equal(60, result.AverageBlockIntervalSeconds);
        Assert.Equal(1.33, result.BlocksPerMinute);
        Assert.Equal(0, result.SecurityFindings);
    }

    [Fact]
    public void Execute_ShouldSurfaceSecurityFindingCountFromAudit()
    {
        var blocks = new[]
        {
            CreateBlock(0, "hash-0", "0", "{}"),
            CreateBlock(1, "bad-hash", "wrong-parent", "{}") with { FinalityMode = "", RaftLogIndex = null }
        };
        var useCase = new GetProjectAnalyticsUseCase(
            new FakeAnalyticsReader(blocks, EmptyState()),
            new InvalidBadHashVerifier());

        var result = useCase.Execute("ProjectA");

        Assert.Equal(4, result.SecurityFindings);
    }

    private static BlockSnapshot CreateBlock(int index, string hash, string previousHash, string data)
    {
        return new BlockSnapshot(
            index,
            new DateTime(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc).AddMinutes(index),
            data,
            previousHash,
            hash,
            "validator",
            "signature",
            0,
            "ProjectA",
            FinalityMode: index > 0 ? "Raft" : string.Empty,
            RaftLogIndex: index > 0 ? index : null);
    }

    private static ProjectAnalyticsState EmptyState()
    {
        return new ProjectAnalyticsState(0, 0, 0, 0, 0, new Dictionary<string, int>());
    }

    private sealed class FakeAnalyticsReader : IProjectAnalyticsReader
    {
        private readonly IReadOnlyList<BlockSnapshot> _blocks;
        private readonly ProjectAnalyticsState _state;

        public FakeAnalyticsReader(IReadOnlyList<BlockSnapshot> blocks, ProjectAnalyticsState state)
        {
            _blocks = blocks;
            _state = state;
        }

        public IReadOnlyList<BlockSnapshot> LoadChain(string projectId) => _blocks;

        public ProjectAnalyticsState LoadState(string projectId) => _state;
    }

    private sealed class AlwaysValidBlockAuditVerifier : IBlockAuditVerifier
    {
        public bool HasValidHash(BlockSnapshot block) => true;

        public bool HasValidSignature(BlockSnapshot block) => true;

    }

    private sealed class InvalidBadHashVerifier : IBlockAuditVerifier
    {
        public bool HasValidHash(BlockSnapshot block) => block.Hash != "bad-hash";

        public bool HasValidSignature(BlockSnapshot block) => block.Hash != "bad-hash";

    }
}
