namespace Blockchain.Node.Services;

public enum P2PNodeRole
{
    Full,
    Bootstrap
}

public sealed class P2POptions
{
    public string NodeRole { get; set; } = nameof(P2PNodeRole.Full);
    public string NodeId { get; set; } = string.Empty;
    public string PublicUrl { get; set; } = string.Empty;
    public string SyncToken { get; set; } = string.Empty;
    public string RegistrationToken { get; set; } = string.Empty;
    public string[] BootstrapPeers { get; set; } = Array.Empty<string>();

    public P2PNodeRole Role =>
        Enum.TryParse<P2PNodeRole>(NodeRole, ignoreCase: true, out var role)
            ? role
            : P2PNodeRole.Full;

    public string EffectiveNodeId =>
        string.IsNullOrWhiteSpace(NodeId)
            ? Environment.MachineName
            : NodeId.Trim();

    public string NormalizedPublicUrl => NormalizeUrl(PublicUrl);

    public IReadOnlyList<string> NormalizedBootstrapPeers => BootstrapPeers
        .Select(NormalizeUrl)
        .Where(url => !string.IsNullOrWhiteSpace(url))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string NormalizeUrl(string? url) => string.IsNullOrWhiteSpace(url)
        ? string.Empty
        : url.Trim().TrimEnd('/');
}
