using Blockchain.Core.Consensus;
using Blockchain.Node.Services;

namespace Blockchain.Tests;

public sealed class GrpcProjectMapperTests
{
    [Fact]
    public void ContributionProof_ShouldRoundTripThroughGrpcModel()
    {
        var proof = new ContributionProof(
            "ProjectA",
            42,
            "producer-pk",
            17,
            "snapshot-hash",
            new[] { "h1", "h2" });

        var model = GrpcProjectMapper.ToContributionProofModel(proof);
        var roundTrip = GrpcProjectMapper.ToContributionProof(model);

        Assert.Equal(proof.ProjectId, roundTrip.ProjectId);
        Assert.Equal(proof.Epoch, roundTrip.Epoch);
        Assert.Equal(proof.ProducerPublicKey, roundTrip.ProducerPublicKey);
        Assert.Equal(proof.ProducerScore, roundTrip.ProducerScore);
        Assert.Equal(proof.ScoreSnapshotHash, roundTrip.ScoreSnapshotHash);
        Assert.Equal(proof.EvidenceBlockHashes, roundTrip.EvidenceBlockHashes);
    }
}
