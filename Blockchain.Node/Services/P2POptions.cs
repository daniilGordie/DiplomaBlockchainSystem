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
    public int DiscoveryIntervalSeconds { get; set; } = 60;
    public string IdentityKeyPath { get; set; } = string.Empty;
    public bool AllowRegistrationTokenFallback { get; set; } = false;
    public int RegistrationClockSkewSeconds { get; set; } = 300;
    public int MaxRegisteredPeers { get; set; } = 1000;
    public int MaxRegistrationsPerMinutePerAddress { get; set; } = 30;
    public P2PIrohOptions Iroh { get; set; } = new();

    public P2PNodeRole Role =>
        Enum.TryParse<P2PNodeRole>(NodeRole, ignoreCase: true, out var role)
            ? role
            : P2PNodeRole.Full;

    public string EffectiveNodeId =>
        string.IsNullOrWhiteSpace(NodeId)
            ? Environment.MachineName
            : NodeId.Trim();

    public string NormalizedPublicUrl => NormalizeUrl(PublicUrl);

    public string EffectiveIdentityKeyPath =>
        string.IsNullOrWhiteSpace(IdentityKeyPath)
            ? Path.Combine(AppContext.BaseDirectory, "node-identity.p256.key")
            : IdentityKeyPath.Trim();

    public IReadOnlyList<string> NormalizedBootstrapPeers => BootstrapPeers
        .Select(NormalizeUrl)
        .Where(url => !string.IsNullOrWhiteSpace(url))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string NormalizeUrl(string? url) => string.IsNullOrWhiteSpace(url)
        ? string.Empty
        : url.Trim().TrimEnd('/');
}

public sealed class P2PIrohOptions
{
    public bool Enabled { get; set; }
    public string SidecarUrl { get; set; } = "http://127.0.0.1:49152";
    public string LocalApiToken { get; set; } = string.Empty;
    public int PollIntervalMilliseconds { get; set; } = 1000;

    public string NormalizedSidecarUrl => P2POptions.NormalizeUrl(SidecarUrl);
}
