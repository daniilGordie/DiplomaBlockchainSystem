using Blockchain.Application.Analytics;

namespace Blockchain.Tests;

public class GetSecurityAuditUseCaseTests
{
    [Fact]
    public void Execute_ShouldReturnValidAuditForCleanChain()
    {
        var reader = new FakeAnalyticsReader(new[]
        {
            CreateBlock(index: 0, hash: "hash-0", previousHash: "0"),
            CreateBlock(index: 1, hash: "hash-1", previousHash: "hash-0")
        });
        var useCase = new GetSecurityAuditUseCase(reader, new FakeBlockAuditVerifier());

        var result = useCase.Execute("ProjectA");

        Assert.True(result.ChainValid);
        Assert.Equal(2, result.CheckedBlocks);
        Assert.Equal(0, result.FindingCount);
        Assert.Contains(result.Items, item => item.CheckName == "Audit result");
    }

    [Fact]
    public void Execute_ShouldReportHashSignatureFinalityAndLinkageFindings()
    {
        var reader = new FakeAnalyticsReader(new[]
        {
            CreateBlock(index: 0, hash: "hash-0", previousHash: "0"),
            CreateBlock(index: 1, hash: "bad-hash", previousHash: "wrong-parent") with { FinalityMode = "", RaftLogIndex = null }
        });
        var verifier = new FakeBlockAuditVerifier(
            invalidHashes: new[] { "bad-hash" },
            invalidSignatures: new[] { "bad-hash" });
        var useCase = new GetSecurityAuditUseCase(reader, verifier);

        var result = useCase.Execute("ProjectA");

        Assert.False(result.ChainValid);
        Assert.Equal(1, result.InvalidHashes);
        Assert.Equal(1, result.InvalidSignatures);
        Assert.Equal(1, result.InvalidFinalityMetadata);
        Assert.Equal(1, result.BrokenLinks);
        Assert.Equal(4, result.FindingCount);
    }

    [Fact]
    public void Build_ShouldAcceptPersistedRaftFinalityMetadata()
    {
        var blocks = new[]
        {
            CreateBlock(index: 0, hash: "hash-0", previousHash: "0"),
            CreateBlock(index: 1, hash: "hash-1", previousHash: "hash-0") with { FinalityMode = "Raft", RaftLogIndex = 1 }
        };

        var result = GetSecurityAuditUseCase.Build(blocks, new FakeBlockAuditVerifier());

        Assert.True(result.ChainValid);
        Assert.Equal(0, result.InvalidFinalityMetadata);
        Assert.Contains(result.Items, item => item.Details.Contains("PoC/Raft finality", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ShouldReportMissingFinalityMetadata()
    {
        var blocks = new[]
        {
            CreateBlock(index: 0, hash: "hash-0", previousHash: "0"),
            CreateBlock(index: 1, hash: "hash-1", previousHash: "hash-0") with { FinalityMode = "", RaftLogIndex = null }
        };

        var result = GetSecurityAuditUseCase.Build(blocks, new FakeBlockAuditVerifier());

        Assert.False(result.ChainValid);
        Assert.Equal(1, result.InvalidFinalityMetadata);
        Assert.Contains(result.Items, item => item.CheckName == "Finality metadata");
    }

    private static BlockSnapshot CreateBlock(int index, string hash, string previousHash)
    {
        return new BlockSnapshot(
            index,
            DateTime.UtcNow.AddMinutes(index),
            "{}",
            previousHash,
            hash,
            "validator",
            "signature",
            0,
            "ProjectA",
            FinalityMode: index > 0 ? "Raft" : string.Empty,
            RaftLogIndex: index > 0 ? index : null);
    }

    private sealed class FakeAnalyticsReader : IProjectAnalyticsReader
    {
        private readonly IReadOnlyList<BlockSnapshot> _blocks;

        public FakeAnalyticsReader(IReadOnlyList<BlockSnapshot> blocks)
        {
            _blocks = blocks;
        }

        public IReadOnlyList<BlockSnapshot> LoadChain(string projectId) => _blocks;

        public ProjectAnalyticsState LoadState(string projectId)
        {
            return new ProjectAnalyticsState(0, 0, 0, 0, 0, new Dictionary<string, int>());
        }
    }

    private sealed class FakeBlockAuditVerifier : IBlockAuditVerifier
    {
        private readonly HashSet<string> _invalidHashes;
        private readonly HashSet<string> _invalidSignatures;

        public FakeBlockAuditVerifier(
            IEnumerable<string>? invalidHashes = null,
            IEnumerable<string>? invalidSignatures = null)
        {
            _invalidHashes = new HashSet<string>(invalidHashes ?? Array.Empty<string>(), StringComparer.Ordinal);
            _invalidSignatures = new HashSet<string>(invalidSignatures ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        public bool HasValidHash(BlockSnapshot block) => !_invalidHashes.Contains(block.Hash);

        public bool HasValidSignature(BlockSnapshot block) => !_invalidSignatures.Contains(block.Hash);

    }
}
