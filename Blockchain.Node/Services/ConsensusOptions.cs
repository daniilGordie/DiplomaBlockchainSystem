namespace Blockchain.Node.Services;

public sealed class ConsensusOptions
{
    public bool EnableProofOfContributionValidation { get; set; } = true;
    public bool RequireProofOfWork { get; set; }
    public bool AcceptP2PBlocksAsFinal { get; set; }
    public string FinalityMode { get; set; } = ConsensusFinalityModes.Raft;
}

public static class ConsensusFinalityModes
{
    public const string Immediate = "Immediate";
    public const string Raft = "Raft";
}

public static class ConsensusConfigurationPolicy
{
    public const string NetworkConsensusEngine = "Proof of Contribution + DotNext Raft";
    public const string LocalConsensusEngine = "Local immediate finality";

    public static string GetEngineName(NexusNodeOptions node) =>
        node.IsLocal ? LocalConsensusEngine : NetworkConsensusEngine;

    public static IReadOnlyList<string> Validate(ConsensusOptions consensus, NexusNodeOptions node)
    {
        var errors = new List<string>();
        if (!Enum.TryParse<NexusNodeRole>(node.Role, ignoreCase: true, out _))
        {
            errors.Add($"Node:Role '{node.Role}' is not supported.");
            return errors;
        }

        if (node.IsLocal)
        {
            if (!string.Equals(consensus.FinalityMode, ConsensusFinalityModes.Immediate, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Local nodes must use Consensus:FinalityMode=Immediate.");
            }

            if (consensus.AcceptP2PBlocksAsFinal)
            {
                errors.Add("Local nodes cannot enable Consensus:AcceptP2PBlocksAsFinal.");
            }

            return errors;
        }

        if (!string.Equals(consensus.FinalityMode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Network nodes must use Consensus:FinalityMode=Raft.");
        }

        if (!consensus.EnableProofOfContributionValidation)
        {
            errors.Add("Network nodes must enable Consensus:EnableProofOfContributionValidation.");
        }

        if (consensus.RequireProofOfWork)
        {
            errors.Add("Network nodes cannot enable Consensus:RequireProofOfWork; Proof of Contribution is the network proposal policy.");
        }

        if (consensus.AcceptP2PBlocksAsFinal)
        {
            errors.Add("Network nodes cannot enable Consensus:AcceptP2PBlocksAsFinal; blocks must be finalized by DotNext Raft.");
        }

        return errors;
    }

    public static void EnsureValid(ConsensusOptions consensus, NexusNodeOptions node)
    {
        var errors = Validate(consensus, node);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid consensus configuration: " + string.Join(" ", errors));
        }
    }
}

public sealed class ConsensusConfigurationValidationService : IHostedService
{
    private readonly ConsensusOptions _consensus;
    private readonly NexusNodeOptions _node;

    public ConsensusConfigurationValidationService(
        Microsoft.Extensions.Options.IOptions<ConsensusOptions> consensus,
        Microsoft.Extensions.Options.IOptions<NexusNodeOptions> node)
    {
        _consensus = consensus.Value;
        _node = node.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ConsensusConfigurationPolicy.EnsureValid(_consensus, _node);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
