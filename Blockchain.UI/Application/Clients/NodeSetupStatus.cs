namespace Blockchain.UI.Application.Clients;

public sealed record NodeSetupStatus(
    string NodeId,
    string NodeRole,
    string Role,
    string PublicUrl,
    string[] BootstrapPeers,
    bool SyncTokenConfigured,
    bool NodeIdentityConfigured,
    string NodeIdentityFingerprint,
    bool RegistrationTokenFallbackEnabled,
    bool IrohEnabled,
    string IrohSidecarUrl,
    bool IrohSidecarHealthy,
    string IrohNodeId,
    string IrohRelayUrl,
    int DiscoveryIntervalSeconds,
    string NodeVersion,
    string ProtocolVersion);

public sealed record MigrationChecklist(IReadOnlyList<MigrationCheckItem> Checks);

public sealed record MigrationCheckItem(string Name, bool Passed, string Detail);

public sealed record SetupPlanRequest(
    string Mode,
    string PublicUrl,
    string BootstrapGrpcUrl,
    string NodeId,
    string RelayMode,
    string OraclePublicKey);

public sealed record SetupPlanResponse(
    bool Valid,
    string Mode,
    string FileName,
    string EnvContent,
    IReadOnlyList<string> Messages);

public sealed record UpdateCheckStatus(
    bool Configured,
    string CurrentNodeVersion,
    string LatestNodeVersion,
    string CurrentProtocolVersion,
    string LatestProtocolVersion,
    string CurrentIrohSidecarVersion,
    string LatestIrohSidecarVersion,
    bool UpdateAvailable,
    bool ProtocolCompatible,
    bool IrohSidecarCompatible,
    string ManifestUrl,
    string Warning);
