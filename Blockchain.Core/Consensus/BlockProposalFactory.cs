using System.Text.Json;

namespace Blockchain.Core.Consensus;

public sealed class BlockProposalFactory
{
    private readonly ContributionScoreService _scoreService;
    private readonly ProducerSelector _producerSelector;

    public BlockProposalFactory(
        ContributionScoreService scoreService,
        ProducerSelector producerSelector)
    {
        _scoreService = scoreService;
        _producerSelector = producerSelector;
    }

    public BlockProposalBuildResult BuildImplicitProposal(Block block)
    {
        string projectId = ChannelName.Normalize(block.ChannelId);
        long epoch = block.Index;
        var snapshot = _scoreService.BuildSnapshot(projectId, epoch);
        var selected = _producerSelector.Select(snapshot);

        if (selected == null)
        {
            if (TryBuildBootstrapProjectProposal(block, snapshot, out var bootstrapProposal))
            {
                return BlockProposalBuildResult.Accept(bootstrapProposal);
            }

            return BlockProposalBuildResult.Reject("no eligible producer");
        }

        var proof = new ContributionProof(
            selected.ProjectId,
            selected.Epoch,
            selected.ProducerPublicKey,
            selected.ProducerScore,
            selected.ScoreSnapshotHash,
            selected.EvidenceBlockHashes);

        return BlockProposalBuildResult.Accept(new BlockProposal(block, proof, DateTime.UtcNow));
    }

    private static bool TryBuildBootstrapProjectProposal(
        Block block,
        ContributionSnapshot snapshot,
        out BlockProposal proposal)
    {
        proposal = default!;

        if (!string.Equals(ChannelName.Normalize(block.ChannelId), "System", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(block.ValidatorPublicKey) ||
            !IsBootstrapCreateProject(block.Data))
        {
            return false;
        }

        var proof = new ContributionProof(
            snapshot.ProjectId,
            snapshot.Epoch,
            block.ValidatorPublicKey,
            0,
            snapshot.SnapshotHash,
            Array.Empty<string>());

        proposal = new BlockProposal(block, proof, DateTime.UtcNow);
        return true;
    }

    internal static bool IsBootstrapCreateProject(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            string type = root.TryGetProperty("Type", out var typeProp)
                ? typeProp.GetString() ?? string.Empty
                : string.Empty;
            string projectId = root.TryGetProperty("ProjectId", out var projectProp)
                ? projectProp.GetString() ?? string.Empty
                : string.Empty;
            string user = root.TryGetProperty("User", out var userProp)
                ? userProp.GetString() ?? string.Empty
                : string.Empty;

            return type.Equals("CreateProject", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(projectId) &&
                   !string.IsNullOrWhiteSpace(user);
        }
        catch
        {
            return false;
        }
    }
}

public sealed record BlockProposalBuildResult(
    bool Accepted,
    string Reason,
    BlockProposal? Proposal)
{
    public static BlockProposalBuildResult Accept(BlockProposal proposal) => new(true, "accepted", proposal);
    public static BlockProposalBuildResult Reject(string reason) => new(false, reason, null);
}
