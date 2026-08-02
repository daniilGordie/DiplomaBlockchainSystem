using System.Text.Json;
using System.Text.Json.Serialization;

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

public sealed record NetworkStatus(
    string NodeRole,
    bool IsConsensusMember,
    bool IsEdge,
    string NodeId,
    string NodeIdentityFingerprint,
    string NetworkId,
    string TrustedBootstrapFingerprint,
    bool IrohEnabled,
    bool IrohSidecarHealthy,
    string IrohPublicUrl,
    string[] BootstrapPeers,
    IReadOnlyList<NetworkPeerStatus> KnownPeers,
    string FinalityMode,
    string ConsensusEngine,
    bool ProofOfContributionValidationEnabled,
    bool LocalRaftRequested,
    string RaftTransport,
    bool LocalRaftConfigured,
    string RaftNodeId,
    string RaftPublicEndPoint,
    IReadOnlyList<NetworkChannelStatus> Channels,
    EdgeSyncStatus EdgeSync,
    EdgeProposalForwardingStatus EdgeProposalForwarding,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record NetworkPeerStatus(
    string Url,
    string NodeId,
    string Role,
    string Transport,
    string LastSeen,
    string LastFailure,
    bool IsTrusted);

public sealed record NetworkChannelStatus(
    string ChannelId,
    int LatestIndex,
    string LatestHash,
    string FinalityMode,
    long? RaftLogIndex,
    DateTime? CommittedAtUtc);

public sealed record EdgeSyncStatus(
    bool Enabled,
    string State,
    int LastAppliedBlocks,
    int LastChangedChannels,
    DateTime? LastSyncUtc,
    string LastPeer,
    string LastError);

public sealed record EdgeProposalForwardingStatus(
    bool Enabled,
    int PendingCount,
    string State,
    string LastChannelId,
    string LastPeer,
    string LastMessage,
    DateTime? LastAttemptUtc,
    long SuccessCount,
    long FailureCount);

public sealed record MigrationChecklist(IReadOnlyList<MigrationCheckItem> Checks);

public sealed record MigrationCheckItem(string Name, bool Passed, string Detail);

public sealed record SetupPlanRequest(
    string Mode,
    string PublicUrl,
    string BootstrapGrpcUrl,
    string NodeId,
    string RelayMode,
    string OraclePublicKey,
    string ConnectionInvite = "");

public sealed record SetupPlanResponse(
    bool Valid,
    string Mode,
    string FileName,
    string EnvContent,
    IReadOnlyList<string> Messages);

public sealed record SetupStateStatus(
    string State,
    string NetworkId,
    string NodeId,
    string Role,
    bool FullNodeConfigured);

public sealed record CreateNetworkSetupRequest(
    string NetworkName,
    string Mode,
    string? NetworkId,
    string? NodeId,
    string? PublicHttpUrl,
    string? PublicGrpcUrl,
    string RaftTransport,
    string? RaftPublicEndPoint,
    string? IrohNodeId,
    string? BootstrapIrohUrl);

public sealed record JoinNetworkSetupRequest(string Invite, string Role, string? NodeId);
public sealed record LocalNodeSetupRequest(string? NetworkName, string? NetworkId, string? NodeId);
public sealed record InviteValidationRequest(string Invite);

public sealed record NetworkInviteDocument(
    int SchemaVersion,
    string ProtocolVersion,
    string NetworkId,
    string NetworkName,
    string BootstrapNodeId,
    string BootstrapPublicKey,
    string BootstrapFingerprint,
    string BootstrapIrohUrl,
    string BootstrapHttpUrl,
    string BootstrapGrpcUrl,
    IReadOnlyList<string> SupportedRaftTransports,
    string SuggestedRole,
    DateTimeOffset ExpiresAtUtc,
    string Nonce,
    string Signature);

public sealed record InviteValidationResponse(bool Valid, string Message, NetworkInviteDocument? Invite);

public sealed record SetupApplyResponse(
    bool Success,
    string Message,
    IReadOnlyDictionary<string, string> Configuration,
    NetworkInviteDocument? Invite,
    string NextState);

public sealed record PeerTrustRequest(
    string Url,
    bool IsTrusted,
    string AdminToken);

public sealed record PeerTrustResponse(
    bool Success,
    string Message);

public sealed record PeerRoleRequest(
    string Url,
    string Role,
    string AdminToken);

public sealed record PeerRoleResponse(
    bool Success,
    string Message,
    string Role);

public sealed record NetworkSyncRequest(string AdminToken);

public sealed record NetworkInvite(
    string NetworkId,
    string BootstrapHttpUrl,
    string BootstrapGrpcUrl,
    string BootstrapIrohUrl,
    string TrustedBootstrapNodeId,
    string TrustedBootstrapRole,
    string TrustedBootstrapFingerprint,
    string SuggestedRole,
    DateTime CreatedAtUtc,
    string Token);

public sealed record IntentListStatus(IReadOnlyList<IntentStatusItem> Intents);

public sealed record IntentStatusItem(
    string IntentId,
    string CorrelationId,
    string OperationType,
    string Status,
    int AttemptCount,
    DateTime? LastAttemptAtUtc,
    DateTime? NextAttemptAtUtc,
    string LastError,
    string Destination,
    string CommittedBlockHash,
    long? CommittedBlockIndex,
    string ProposalId,
    IReadOnlyList<IntentTransitionStatus> History);

public sealed record IntentTransitionStatus(
    string Status,
    DateTime ChangedAtUtc,
    string Message,
    string Destination,
    string ProposalId,
    string CommittedBlockHash,
    long? CommittedBlockIndex);

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

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NodeSetupStatus))]
[JsonSerializable(typeof(NetworkStatus))]
[JsonSerializable(typeof(NetworkInvite))]
[JsonSerializable(typeof(IntentListStatus))]
[JsonSerializable(typeof(MigrationChecklist))]
[JsonSerializable(typeof(SetupPlanRequest))]
[JsonSerializable(typeof(SetupPlanResponse))]
[JsonSerializable(typeof(SetupStateStatus))]
[JsonSerializable(typeof(InviteValidationRequest))]
[JsonSerializable(typeof(InviteValidationResponse))]
[JsonSerializable(typeof(CreateNetworkSetupRequest))]
[JsonSerializable(typeof(JoinNetworkSetupRequest))]
[JsonSerializable(typeof(LocalNodeSetupRequest))]
[JsonSerializable(typeof(SetupApplyResponse))]
[JsonSerializable(typeof(PeerTrustRequest))]
[JsonSerializable(typeof(PeerTrustResponse))]
[JsonSerializable(typeof(PeerRoleRequest))]
[JsonSerializable(typeof(PeerRoleResponse))]
[JsonSerializable(typeof(NetworkSyncRequest))]
[JsonSerializable(typeof(EdgeSyncStatus))]
[JsonSerializable(typeof(UpdateCheckStatus))]
internal partial class NodeApiJsonContext : JsonSerializerContext
{
    public static NodeApiJsonContext Indented { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    });
}
