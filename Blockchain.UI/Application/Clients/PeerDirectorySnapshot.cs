namespace Blockchain.UI.Application.Clients;

public sealed record PeerDirectorySnapshot(
    string CurrentNodeId,
    string CurrentPublicUrl,
    string CurrentRole,
    IReadOnlyList<string> BootstrapPeers,
    bool IrohEnabled,
    string IrohSidecarUrl,
    bool NodeIdentityConfigured,
    bool RegistrationTokenFallbackEnabled,
    int DiscoveryIntervalSeconds,
    IReadOnlyList<PeerNodeInfo> Peers);
