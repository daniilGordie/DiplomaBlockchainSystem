namespace Blockchain.Node.Services;

public enum NexusNodeRole
{
    Bootstrap,
    Consensus,
    Edge,
    Local
}

public sealed class NexusNodeOptions
{
    public string Role { get; set; } = nameof(NexusNodeRole.Consensus);

    public NexusNodeRole EffectiveRole =>
        Enum.TryParse<NexusNodeRole>(Role, ignoreCase: true, out var role)
            ? role
            : NexusNodeRole.Consensus;

    public bool IsConsensusMember =>
        EffectiveRole is NexusNodeRole.Bootstrap or NexusNodeRole.Consensus;

    public bool IsEdge => EffectiveRole == NexusNodeRole.Edge;

    public bool IsLocal => EffectiveRole == NexusNodeRole.Local;
}
