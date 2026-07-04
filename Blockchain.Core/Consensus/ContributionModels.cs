using Blockchain.Core;

namespace Blockchain.Core.Consensus;

public sealed record ContributionScore(
    string UserName,
    string PublicKey,
    int Score,
    IReadOnlyList<string> EvidenceBlockHashes);

public sealed record ContributionSnapshot(
    string ProjectId,
    long Epoch,
    IReadOnlyList<ContributionScore> Scores,
    string SnapshotHash);

public sealed record ProducerSelection(
    string ProjectId,
    long Epoch,
    string ProducerPublicKey,
    int ProducerScore,
    string ScoreSnapshotHash,
    IReadOnlyList<string> EvidenceBlockHashes);

public sealed record ContributionProof(
    string ProjectId,
    long Epoch,
    string ProducerPublicKey,
    int ProducerScore,
    string ScoreSnapshotHash,
    IReadOnlyList<string> EvidenceBlockHashes);

public sealed record BlockProposal(
    Block Block,
    ContributionProof ContributionProof,
    DateTime ProposedAtUtc);

public sealed record PoCVerificationResult(
    bool Accepted,
    string Reason)
{
    public static PoCVerificationResult Accept() => new(true, "accepted");
    public static PoCVerificationResult Reject(string reason) => new(false, reason);
}
