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
    public void Execute_ShouldReportHashSignatureProofAndLinkageFindings()
    {
        var reader = new FakeAnalyticsReader(new[]
        {
            CreateBlock(index: 0, hash: "hash-0", previousHash: "0"),
            CreateBlock(index: 1, hash: "bad-hash", previousHash: "wrong-parent")
        });
        var verifier = new FakeBlockAuditVerifier(
            invalidHashes: new[] { "bad-hash" },
            invalidSignatures: new[] { "bad-hash" },
            invalidProofOfWork: new[] { "bad-hash" });
        var useCase = new GetSecurityAuditUseCase(reader, verifier);

        var result = useCase.Execute("ProjectA");

        Assert.False(result.ChainValid);
        Assert.Equal(1, result.InvalidHashes);
        Assert.Equal(1, result.InvalidSignatures);
        Assert.Equal(1, result.InvalidProofOfWork);
        Assert.Equal(1, result.BrokenLinks);
        Assert.Equal(4, result.FindingCount);
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
            "ProjectA");
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
        private readonly HashSet<string> _invalidProofOfWork;

        public FakeBlockAuditVerifier(
            IEnumerable<string>? invalidHashes = null,
            IEnumerable<string>? invalidSignatures = null,
            IEnumerable<string>? invalidProofOfWork = null)
        {
            _invalidHashes = new HashSet<string>(invalidHashes ?? Array.Empty<string>(), StringComparer.Ordinal);
            _invalidSignatures = new HashSet<string>(invalidSignatures ?? Array.Empty<string>(), StringComparer.Ordinal);
            _invalidProofOfWork = new HashSet<string>(invalidProofOfWork ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        public bool HasValidHash(BlockSnapshot block) => !_invalidHashes.Contains(block.Hash);

        public bool HasValidSignature(BlockSnapshot block) => !_invalidSignatures.Contains(block.Hash);

        public bool HasValidProofOfWork(BlockSnapshot block) => !_invalidProofOfWork.Contains(block.Hash);
    }
}
