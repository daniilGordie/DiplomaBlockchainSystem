namespace Blockchain.Application.Analytics;

public sealed class GetSecurityAuditUseCase
{
    private readonly IProjectAnalyticsReader _reader;
    private readonly IBlockAuditVerifier _verifier;

    public GetSecurityAuditUseCase(IProjectAnalyticsReader reader, IBlockAuditVerifier verifier)
    {
        _reader = reader;
        _verifier = verifier;
    }

    public SecurityAuditResult Execute(string projectId)
    {
        var blocks = _reader.LoadChain(projectId)
            .OrderBy(block => block.Index)
            .ToList();

        return Build(blocks, _verifier);
    }

    public static SecurityAuditResult Build(IReadOnlyList<BlockSnapshot> orderedBlocks, IBlockAuditVerifier verifier)
    {
        int invalidHashes = 0;
        int invalidSignatures = 0;
        int invalidProofOfWork = 0;
        int brokenLinks = 0;
        var items = new List<SecurityAuditFinding>();

        for (int i = 0; i < orderedBlocks.Count; i++)
        {
            var current = orderedBlocks[i];

            if (!verifier.HasValidHash(current))
            {
                invalidHashes++;
                items.Add(new SecurityAuditFinding("Critical", "Hash integrity", $"Block #{current.Index} hash does not match its content."));
            }

            if (!verifier.HasValidSignature(current))
            {
                invalidSignatures++;
                items.Add(new SecurityAuditFinding("Critical", "ECDSA signature", $"Block #{current.Index} signature is invalid."));
            }

            if (!verifier.HasValidProofOfWork(current))
            {
                invalidProofOfWork++;
                items.Add(new SecurityAuditFinding("High", "Proof of work", $"Block #{current.Index} does not satisfy the target prefix."));
            }

            string expectedPreviousHash = i == 0 ? "0" : orderedBlocks[i - 1].Hash;
            if (current.PreviousHash != expectedPreviousHash)
            {
                brokenLinks++;
                items.Add(new SecurityAuditFinding("Critical", "Chain linkage", $"Block #{current.Index} previous hash points to an unexpected parent."));
            }
        }

        bool chainValid = invalidHashes == 0
            && invalidSignatures == 0
            && invalidProofOfWork == 0
            && brokenLinks == 0;

        if (chainValid)
        {
            items.Add(new SecurityAuditFinding("Info", "Audit result", "All checked blocks passed hash, signature, proof-of-work, and linkage validation."));
        }

        return new SecurityAuditResult(
            chainValid,
            orderedBlocks.Count,
            invalidHashes,
            invalidSignatures,
            invalidProofOfWork,
            brokenLinks,
            items);
    }
}
