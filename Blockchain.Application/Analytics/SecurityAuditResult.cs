namespace Blockchain.Application.Analytics;

public sealed record SecurityAuditFinding(
    string Severity,
    string CheckName,
    string Details);

public sealed record SecurityAuditResult(
    bool ChainValid,
    int CheckedBlocks,
    int InvalidHashes,
    int InvalidSignatures,
    int InvalidProofOfWork,
    int BrokenLinks,
    IReadOnlyList<SecurityAuditFinding> Items)
{
    public int FindingCount => InvalidHashes + InvalidSignatures + InvalidProofOfWork + BrokenLinks;
}
