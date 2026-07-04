using Blockchain.Core;
using Blockchain.Core.Contracts;

namespace Blockchain.Core.Consensus;

public sealed class PoCVerifier
{
    private readonly ContributionScoreService _scoreService;
    private readonly ProducerSelector _producerSelector;

    public PoCVerifier(
        IChainReader chainReader,
        ISmartContractStateReader stateReader)
        : this(new ContributionScoreService(chainReader, stateReader), new ProducerSelector())
    {
    }

    public PoCVerifier(
        ContributionScoreService scoreService,
        ProducerSelector producerSelector)
    {
        _scoreService = scoreService;
        _producerSelector = producerSelector;
    }

    public PoCVerificationResult Verify(BlockProposal proposal)
    {
        var block = proposal.Block;
        var proof = proposal.ContributionProof;
        string blockProjectId = ChannelName.Normalize(block.ChannelId);
        string proofProjectId = ChannelName.Normalize(proof.ProjectId);

        if (!string.Equals(blockProjectId, proofProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return PoCVerificationResult.Reject("project mismatch");
        }

        if (!string.Equals(block.ValidatorPublicKey, proof.ProducerPublicKey, StringComparison.Ordinal))
        {
            return PoCVerificationResult.Reject("block validator is not the selected producer");
        }

        var snapshot = _scoreService.BuildSnapshot(proofProjectId, proof.Epoch);
        if (!string.Equals(snapshot.SnapshotHash, proof.ScoreSnapshotHash, StringComparison.Ordinal))
        {
            return PoCVerificationResult.Reject("score snapshot hash mismatch");
        }

        var selected = _producerSelector.Select(snapshot);
        if (selected == null)
        {
            if (IsValidBootstrapProjectCreation(block, proof, snapshot))
            {
                return PoCVerificationResult.Accept();
            }

            return PoCVerificationResult.Reject("no eligible producer");
        }

        if (!string.Equals(selected.ProducerPublicKey, proof.ProducerPublicKey, StringComparison.Ordinal))
        {
            return PoCVerificationResult.Reject("producer was not selected for this epoch");
        }

        if (selected.ProducerScore != proof.ProducerScore)
        {
            return PoCVerificationResult.Reject("producer score mismatch");
        }

        if (!selected.EvidenceBlockHashes.OrderBy(hash => hash, StringComparer.Ordinal)
                .SequenceEqual(proof.EvidenceBlockHashes.OrderBy(hash => hash, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return PoCVerificationResult.Reject("evidence block hashes mismatch");
        }

        return PoCVerificationResult.Accept();
    }

    private static bool IsValidBootstrapProjectCreation(
        Block block,
        ContributionProof proof,
        ContributionSnapshot snapshot)
    {
        return string.Equals(ChannelName.Normalize(block.ChannelId), "System", StringComparison.OrdinalIgnoreCase) &&
               BlockProposalFactory.IsBootstrapCreateProject(block.Data) &&
               string.Equals(block.ValidatorPublicKey, proof.ProducerPublicKey, StringComparison.Ordinal) &&
               proof.ProducerScore == 0 &&
               proof.EvidenceBlockHashes.Count == 0 &&
               string.Equals(proof.ProjectId, snapshot.ProjectId, StringComparison.OrdinalIgnoreCase) &&
               proof.Epoch == snapshot.Epoch &&
               string.Equals(proof.ScoreSnapshotHash, snapshot.SnapshotHash, StringComparison.Ordinal);
    }
}
