using Blockchain.Core;
using Blockchain.Node.Services;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace Blockchain.Node.Endpoints;

public static class SetupStatusEndpoints
{
    public static IEndpointRouteBuilder MapSetupStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/setup/status", async (
            IOptions<P2POptions> p2p,
            IOptions<NexusNodeOptions> nodeOptions,
            NodeIdentity nodeIdentity,
            IrohSidecarClient irohSidecar,
            IOptions<NodeVersionOptions> versionOptions) =>
        {
            var options = p2p.Value;
            var irohStatus = options.Iroh.Enabled
                ? await irohSidecar.GetStatusAsync()
                : null;

            return Results.Json(new SetupStatusResponse(
                options.EffectiveNodeId,
                nodeOptions.Value.EffectiveRole.ToString(),
                options.Role.ToString(),
                options.NormalizedPublicUrl,
                options.NormalizedBootstrapPeers.ToArray(),
                !string.IsNullOrWhiteSpace(options.SyncToken),
                !string.IsNullOrWhiteSpace(options.EffectiveIdentityKeyPath),
                Fingerprint(nodeIdentity.PublicKey),
                options.AllowRegistrationTokenFallback,
                options.Iroh.Enabled,
                options.Iroh.NormalizedSidecarUrl,
                irohStatus != null,
                irohStatus?.NodeId ?? string.Empty,
                irohStatus?.RelayUrl ?? string.Empty,
                Math.Clamp(options.DiscoveryIntervalSeconds, 10, 3600),
                versionOptions.Value.NodeVersion,
                versionOptions.Value.ProtocolVersion));
        });

        endpoints.MapGet("/api/node/status", async (
            IConfiguration configuration,
            IOptions<NexusNodeOptions> node,
            IOptions<P2POptions> p2p,
            IOptions<ConsensusOptions> consensus,
            IOptions<RaftOptions> raft,
            NodeIdentity nodeIdentity,
            IrohSidecarClient irohSidecar) =>
        {
            var nodeValue = node.Value;
            var p2pValue = p2p.Value;
            var consensusValue = consensus.Value;
            var raftValue = raft.Value;
            var irohStatus = p2pValue.Iroh.Enabled
                ? await irohSidecar.GetStatusAsync()
                : null;
            var diagnostics = BuildNodeDiagnostics(configuration, nodeValue, p2pValue, consensusValue, raftValue);

            return Results.Json(new NodeStatusResponse(
                nodeValue.EffectiveRole.ToString(),
                nodeValue.IsConsensusMember,
                nodeValue.IsEdge,
                p2pValue.EffectiveNodeId,
                Fingerprint(nodeIdentity.PublicKey),
                p2pValue.Role.ToString(),
                p2pValue.NormalizedPublicUrl,
                p2pValue.NormalizedBootstrapPeers.ToArray(),
                p2pValue.Iroh.Enabled,
                p2pValue.Iroh.NormalizedSidecarUrl,
                irohStatus != null,
                irohStatus?.NodeId ?? string.Empty,
                irohStatus?.PublicUrl ?? string.Empty,
                irohStatus?.RelayUrl ?? string.Empty,
                consensusValue.FinalityMode,
                consensusValue.EnableProofOfContributionValidation,
                consensusValue.RequireProofOfWork,
                consensusValue.AcceptP2PBlocksAsFinal,
                string.Equals(consensusValue.FinalityMode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase) && nodeValue.IsConsensusMember,
                raftValue.HasMinimumConfiguration,
                raftValue.NodeId,
                raftValue.PublicEndPoint,
                raftValue.Peers.Select(peer => new NodeStatusRaftPeer(peer.Id, peer.EndPoint)).ToArray(),
                diagnostics.Errors,
                diagnostics.Warnings));
        });

        endpoints.MapGet("/api/network/status", async (
            IConfiguration configuration,
            IOptions<NexusNodeOptions> node,
            IOptions<P2POptions> p2p,
            IOptions<ConsensusOptions> consensus,
            IOptions<RaftOptions> raft,
            NodeIdentity nodeIdentity,
            IrohSidecarClient irohSidecar,
            IPeerStore peerStore,
            IBlockStore blockStore) =>
        {
            var nodeValue = node.Value;
            var p2pValue = p2p.Value;
            var consensusValue = consensus.Value;
            var raftValue = raft.Value;
            var irohStatus = p2pValue.Iroh.Enabled
                ? await irohSidecar.GetStatusAsync()
                : null;
            var diagnostics = BuildNodeDiagnostics(configuration, nodeValue, p2pValue, consensusValue, raftValue);
            var peers = peerStore.LoadPeerInfos()
                .Select(peer => new NetworkPeerResponse(
                    peer.Url,
                    peer.NodeId,
                    peer.Role,
                    IrohSidecarClient.IsIrohPeerUrl(peer.Url) ? "Iroh" : "HttpGrpc",
                    peer.LastSeen ?? string.Empty,
                    peer.LastFailure ?? string.Empty,
                    peer.IsTrusted))
                .ToArray();
            var channels = blockStore.GetKnownChannels()
                .Select(channelId =>
                {
                    var latest = blockStore.GetLatestBlock(channelId);
                    var metadata = latest == null ? null : blockStore.GetFinalityMetadata(latest.Hash);
                    return new NetworkChannelStatusResponse(
                        channelId,
                        latest?.Index ?? -1,
                        latest?.Hash ?? string.Empty,
                        metadata?.FinalityMode ?? string.Empty,
                        metadata?.RaftLogIndex,
                        metadata?.CommittedAtUtc);
                })
                .ToArray();

            return Results.Json(new NetworkStatusResponse(
                nodeValue.EffectiveRole.ToString(),
                nodeValue.IsConsensusMember,
                nodeValue.IsEdge,
                p2pValue.EffectiveNodeId,
                Fingerprint(nodeIdentity.PublicKey),
                p2pValue.Iroh.Enabled,
                irohStatus != null,
                irohStatus?.PublicUrl ?? string.Empty,
                p2pValue.NormalizedBootstrapPeers.ToArray(),
                peers,
                consensusValue.FinalityMode,
                consensusValue.EnableProofOfContributionValidation,
                string.Equals(consensusValue.FinalityMode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase) && nodeValue.IsConsensusMember,
                raftValue.HasMinimumConfiguration,
                raftValue.NodeId,
                raftValue.PublicEndPoint,
                channels,
                0,
                diagnostics.Errors,
                diagnostics.Warnings));
        });

        endpoints.MapGet("/api/setup/diagnostics", (
            IConfiguration configuration,
            IOptions<NexusNodeOptions> node,
            IOptions<P2POptions> p2p,
            IOptions<ConsensusOptions> consensus,
            IOptions<RaftOptions> raft) =>
        {
            var diagnostics = BuildNodeDiagnostics(configuration, node.Value, p2p.Value, consensus.Value, raft.Value);
            return Results.Json(new SetupDiagnosticsResponse(
                diagnostics.Errors.Count == 0,
                diagnostics.Errors,
                diagnostics.Warnings));
        });

        endpoints.MapGet("/api/setup/version", (IOptions<NodeVersionOptions> versionOptions) =>
        {
            var version = versionOptions.Value;
            return Results.Json(version);
        });

        endpoints.MapGet("/api/setup/migrations", async (
            IConfiguration configuration,
            IOptions<P2POptions> p2p,
            IrohSidecarClient irohSidecar) =>
        {
            var options = p2p.Value;
            string dbPath = configuration.GetConnectionString("DefaultNodeDb") ?? "nexus_node_5041.db";
            var checks = new List<MigrationCheck>
            {
                new("database-path", !string.IsNullOrWhiteSpace(dbPath), dbPath),
                new("node-identity-path", !string.IsNullOrWhiteSpace(options.EffectiveIdentityKeyPath), options.EffectiveIdentityKeyPath),
                new("sync-token", !string.IsNullOrWhiteSpace(options.SyncToken), "configured=" + !string.IsNullOrWhiteSpace(options.SyncToken)),
                new("signed-registration", !options.AllowRegistrationTokenFallback, "fallback=" + options.AllowRegistrationTokenFallback),
                new("bootstrap-peers", options.Role == P2PNodeRole.Bootstrap || options.NormalizedBootstrapPeers.Count > 0, string.Join(",", options.NormalizedBootstrapPeers)),
            };

            if (options.Iroh.Enabled)
            {
                var status = await irohSidecar.GetStatusAsync();
                checks.Add(new("iroh-sidecar", status != null, status?.PublicUrl ?? "unavailable"));
                checks.Add(new("iroh-relay", !string.IsNullOrWhiteSpace(status?.RelayUrl), status?.RelayUrl ?? "none"));
            }

            return Results.Json(new MigrationChecklistResponse(checks));
        });

        endpoints.MapPost("/api/setup/plan", (SetupPlanRequest request) =>
        {
            var validation = ValidateSetupPlan(request);
            if (validation.Count > 0)
            {
                return Results.BadRequest(new SetupPlanResponse(false, NormalizeMode(request.Mode), "", "", validation));
            }

            string mode = NormalizeMode(request.Mode);
            string fileName = mode == "bootstrap" ? "bootstrap-node.env" : mode == "consensus" ? "consensus-node.env" : mode == "edge" ? "edge-node.env" : "local-node.env";
            string envContent = BuildSetupEnv(mode, request);
            return Results.Json(new SetupPlanResponse(true, mode, fileName, envContent, BuildSetupWarnings(mode, request)));
        });

        endpoints.MapGet("/api/setup/update-check", async (
            IOptions<NodeVersionOptions> versionOptions,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            var current = versionOptions.Value;
            if (string.IsNullOrWhiteSpace(current.UpdateManifestUrl))
            {
                return Results.Json(UpdateCheckResponse.NotConfigured(current));
            }

            try
            {
                using var http = httpClientFactory.CreateClient();
                var manifest = await http.GetFromJsonAsync<UpdateManifest>(current.UpdateManifestUrl, cancellationToken);
                if (manifest == null)
                {
                    return Results.Json(UpdateCheckResponse.Failed(current, "Update manifest returned an empty response."));
                }

                bool updateAvailable = IsDifferent(current.NodeVersion, manifest.NodeVersion);
                bool protocolCompatible = string.Equals(current.ProtocolVersion, manifest.ProtocolVersion, StringComparison.OrdinalIgnoreCase);
                bool sidecarCompatible = string.IsNullOrWhiteSpace(manifest.IrohSidecarVersion)
                    || string.Equals(current.IrohSidecarVersion, manifest.IrohSidecarVersion, StringComparison.OrdinalIgnoreCase);
                string warning = protocolCompatible && sidecarCompatible
                    ? string.Empty
                    : "Update requires coordinated node, protocol, or Iroh sidecar migration.";

                return Results.Json(new UpdateCheckResponse(
                    true,
                    current.NodeVersion,
                    manifest.NodeVersion,
                    current.ProtocolVersion,
                    manifest.ProtocolVersion,
                    current.IrohSidecarVersion,
                    manifest.IrohSidecarVersion,
                    updateAvailable,
                    protocolCompatible,
                    sidecarCompatible,
                    current.UpdateManifestUrl,
                    warning));
            }
            catch (Exception ex)
            {
                return Results.Json(UpdateCheckResponse.Failed(current, ex.Message));
            }
        });

        return endpoints;
    }

    private static List<string> ValidateSetupPlan(SetupPlanRequest request)
    {
        var errors = new List<string>();
        string mode = NormalizeMode(request.Mode);
        if (mode is not ("local" or "edge" or "consensus" or "bootstrap"))
        {
            errors.Add("Choose Local node, Join network, Consensus node, or Bootstrap node.");
        }

        if (mode == "bootstrap" && !IsHttpUrl(request.PublicUrl))
        {
            errors.Add("Bootstrap mode requires a public HTTP or HTTPS URL.");
        }

        if ((mode == "edge" || mode == "consensus") && !IsHttpUrl(request.BootstrapGrpcUrl))
        {
            errors.Add("Join network mode requires a bootstrap gRPC HTTP or HTTPS URL.");
        }

        string relayMode = NormalizeRelayMode(request.RelayMode);
        if (relayMode is not ("default" or "staging" or "disabled"))
        {
            errors.Add("Relay mode must be default, staging, or disabled.");
        }

        return errors;
    }

    private static NodeDiagnostics BuildNodeDiagnostics(
        IConfiguration configuration,
        NexusNodeOptions node,
        P2POptions p2p,
        ConsensusOptions consensus,
        RaftOptions raft)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        bool raftFinality = string.Equals(consensus.FinalityMode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase);

        AddValueDiagnostics(configuration, "Node:Role", errors, warnings);
        AddValueDiagnostics(configuration, "Consensus:FinalityMode", errors, warnings);
        AddValueDiagnostics(configuration, "Consensus:ProducerPrivateKeyPassword", errors, warnings);
        AddValueDiagnostics(configuration, "OraclePrivateKeyPassword", errors, warnings);
        AddValueDiagnostics(configuration, "P2P:Iroh:LocalApiToken", errors, warnings);
        AddValueDiagnostics(configuration, "Raft:NodeId", errors, warnings);
        AddValueDiagnostics(configuration, "Raft:PublicEndPoint", errors, warnings);
        AddValueDiagnostics(configuration, "Raft:Peers:0:Id", errors, warnings);
        AddValueDiagnostics(configuration, "Raft:Peers:0:EndPoint", errors, warnings);

        if (node.IsEdge && !p2p.Iroh.Enabled)
        {
            errors.Add("Node:Role=Edge requires P2P:Iroh:Enabled=true.");
        }

        if (node.IsEdge && raftFinality && raft.HasMinimumConfiguration)
        {
            warnings.Add("Edge nodes ignore local Raft configuration. Use Consensus or Bootstrap role for Raft membership.");
        }

        if (node.IsConsensusMember && raftFinality && !raft.HasMinimumConfiguration)
        {
            errors.Add("Consensus/Bootstrap nodes with Consensus:FinalityMode=Raft require Raft:NodeId, Raft:PublicEndPoint, and at least one peer.");
        }

        if (node.IsLocal && raftFinality)
        {
            errors.Add("Node:Role=Local cannot use Consensus:FinalityMode=Raft. Use Immediate finality or change role.");
        }

        if (p2p.Iroh.Enabled && string.IsNullOrWhiteSpace(p2p.Iroh.LocalApiToken))
        {
            errors.Add("P2P:Iroh:LocalApiToken is required when Iroh is enabled.");
        }

        return new NodeDiagnostics(errors, warnings);
    }

    private static void AddValueDiagnostics(
        IConfiguration configuration,
        string key,
        List<string> errors,
        List<string> warnings)
    {
        string? value = configuration[key];
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            errors.Add($"{key} has leading or trailing whitespace.");
        }

        if (value.Contains("change-this", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("FULL_NODE_PUBLIC_IP_OR_DOMAIN", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"{key} still contains a placeholder value.");
        }
    }

    private static IReadOnlyList<string> BuildSetupWarnings(string mode, SetupPlanRequest request)
    {
        var warnings = new List<string>();
        string relayMode = NormalizeRelayMode(request.RelayMode);
        if (mode == "local")
        {
            warnings.Add("Local mode is isolated and will not join the shared blockchain network.");
        }

        if (mode == "edge" && relayMode == "disabled")
        {
            warnings.Add("Disabled Iroh relay mode is not suitable for machines behind NAT.");
        }

        if (mode == "bootstrap" && request.PublicUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Public bootstrap nodes should use HTTPS in production.");
        }

        return warnings;
    }

    private static string BuildSetupEnv(string mode, SetupPlanRequest request)
    {
        string nodeId = string.IsNullOrWhiteSpace(request.NodeId) ? NewNodeId(mode) : request.NodeId.Trim();
        string relayMode = NormalizeRelayMode(request.RelayMode);
        var lines = new List<string>
        {
            "ASPNETCORE_ENVIRONMENT=Production",
            "NODE_DB_PASSWORD=" + NewSecret(),
            "NODE_ROLE=" + (mode switch
            {
                "bootstrap" => "Bootstrap",
                "consensus" => "Consensus",
                "edge" => "Edge",
                _ => "Local"
            }),
            "NODE_ADMIN_TOKEN=" + NewSecret(),
            "WEBHOOK_SECRET=" + NewSecret(),
            "ORACLE_PUBLIC_KEY=" + (string.IsNullOrWhiteSpace(request.OraclePublicKey) ? "auto" : request.OraclePublicKey.Trim()),
            "ORACLE_PRIVATE_KEY_PASSWORD=" + NewSecret(),
            "ORACLE_KEY_PATH=/data/oracle_key.dat",
            "CONSENSUS_ENABLE_POC=" + (mode == "local" ? "false" : "true"),
            "CONSENSUS_REQUIRE_PROOF_OF_WORK=" + (mode == "local" ? "true" : "false"),
            "CONSENSUS_ACCEPT_P2P_BLOCKS_AS_FINAL=" + (mode == "local" ? "true" : "false"),
            "CONSENSUS_FINALITY_MODE=" + (mode == "local" ? ConsensusFinalityModes.Immediate : ConsensusFinalityModes.Raft),
            "CONSENSUS_PRODUCER_KEY_PATH=/data/producer-key.dat",
            "CONSENSUS_PRODUCER_KEY_PASSWORD=" + NewSecret(),
            "P2P_NODE_ID=" + nodeId,
            "P2P_SYNC_TOKEN=" + NewSecret(),
            "P2P_REGISTRATION_TOKEN=",
            "P2P_ALLOW_REGISTRATION_TOKEN_FALLBACK=false",
            "P2P_IDENTITY_KEY_PATH=/data/node-identity.p256.key",
            "P2P_DISCOVERY_INTERVAL_SECONDS=60"
        };

        if (mode == "bootstrap")
        {
            lines.Add("NODE_HTTP_PORT=7041");
            lines.Add("NODE_GRPC_PORT=7141");
            lines.Add("RAFT_PORT=6041");
            lines.Add("RAFT_NODE_ID=" + nodeId);
            lines.Add("RAFT_PUBLIC_ENDPOINT=bootstrap-node:6041");
            lines.Add("RAFT_LOG_PATH=/data/raft-log");
            lines.Add("RAFT_USE_PERSISTENT_MEMBERSHIP=true");
            lines.Add("RAFT_MEMBERSHIP_PATH=/data/raft-membership");
            lines.Add("RAFT_SNAPSHOT_PATH=/data/raft-snapshots");
            lines.Add("RAFT_PEER_ID=");
            lines.Add("RAFT_PEER_ENDPOINT=");
            lines.Add("P2P_PUBLIC_URL=" + request.PublicUrl.Trim());
            lines.Add("P2P_MAX_REGISTERED_PEERS=5000");
            lines.Add("P2P_MAX_REGISTRATIONS_PER_MINUTE_PER_ADDRESS=30");
        }
        else
        {
            lines.Add("NODE_HTTP_PORT=7042");
            lines.Add("NODE_GRPC_PORT=7142");
            lines.Add("P2P_BOOTSTRAP_GRPC_URL=" + (mode == "local" ? "" : request.BootstrapGrpcUrl.Trim()));
            lines.Add("IROH_LOCAL_API_TOKEN=" + NewSecret());
            lines.Add("IROH_SECRET_KEY_PATH=/data/iroh-secret.key");
            lines.Add("IROH_RELAY_MODE=" + relayMode);

            if (mode == "consensus")
            {
                lines.Add("RAFT_PORT=6042");
                lines.Add("RAFT_NODE_ID=" + nodeId);
                lines.Add("RAFT_PUBLIC_ENDPOINT=consensus-node:6042");
                lines.Add("RAFT_LOG_PATH=/data/raft-log");
                lines.Add("RAFT_USE_PERSISTENT_MEMBERSHIP=true");
                lines.Add("RAFT_MEMBERSHIP_PATH=/data/raft-membership");
                lines.Add("RAFT_SNAPSHOT_PATH=/data/raft-snapshots");
                lines.Add("RAFT_PEER_ID=bootstrap-main-1");
                lines.Add("RAFT_PEER_ENDPOINT=");
            }
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string NormalizeMode(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "join" or "edge" or "full" or "fullnode" or "full-node" => "edge",
            "consensus" or "consensus-node" or "raft" or "raft-member" => "consensus",
            "boot" or "bootstrap" or "bootstrap-node" => "bootstrap",
            "local" or "local-node" => "local",
            var value => value
        };

    private static string NormalizeRelayMode(string? relayMode) =>
        string.IsNullOrWhiteSpace(relayMode) ? "default" : relayMode.Trim().ToLowerInvariant();

    private static string NewNodeId(string mode)
    {
        string value = $"{mode}-{Guid.NewGuid():N}";
        return value[..Math.Min(value.Length, mode.Length + 9)];
    }

    private static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static bool IsDifferent(string current, string latest) =>
        !string.IsNullOrWhiteSpace(latest)
        && !string.Equals(current, latest, StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(string publicKey)
    {
        byte[] raw = Convert.FromBase64String(publicKey);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(raw);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}

public sealed record SetupStatusResponse(
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

public sealed record NodeStatusResponse(
    string NodeRole,
    bool IsConsensusMember,
    bool IsEdge,
    string NodeId,
    string NodeIdentityFingerprint,
    string P2PRole,
    string PublicUrl,
    string[] BootstrapPeers,
    bool IrohEnabled,
    string IrohSidecarUrl,
    bool IrohSidecarHealthy,
    string IrohNodeId,
    string IrohPublicUrl,
    string IrohRelayUrl,
    string FinalityMode,
    bool ProofOfContributionValidationEnabled,
    bool RequireProofOfWork,
    bool AcceptP2PBlocksAsFinal,
    bool LocalRaftRequested,
    bool LocalRaftConfigured,
    string RaftNodeId,
    string RaftPublicEndPoint,
    IReadOnlyList<NodeStatusRaftPeer> RaftPeers,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record NodeStatusRaftPeer(string Id, string EndPoint);

public sealed record NodeDiagnostics(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed record NetworkStatusResponse(
    string NodeRole,
    bool IsConsensusMember,
    bool IsEdge,
    string NodeId,
    string NodeIdentityFingerprint,
    bool IrohEnabled,
    bool IrohSidecarHealthy,
    string IrohPublicUrl,
    string[] BootstrapPeers,
    IReadOnlyList<NetworkPeerResponse> KnownPeers,
    string FinalityMode,
    bool ProofOfContributionValidationEnabled,
    bool LocalRaftRequested,
    bool LocalRaftConfigured,
    string RaftNodeId,
    string RaftPublicEndPoint,
    IReadOnlyList<NetworkChannelStatusResponse> Channels,
    int PendingProposalCount,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record NetworkPeerResponse(
    string Url,
    string NodeId,
    string Role,
    string Transport,
    string LastSeen,
    string LastFailure,
    bool IsTrusted);

public sealed record NetworkChannelStatusResponse(
    string ChannelId,
    int LatestIndex,
    string LatestHash,
    string FinalityMode,
    long? RaftLogIndex,
    DateTime? CommittedAtUtc);

public sealed record SetupDiagnosticsResponse(
    bool Ready,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record MigrationChecklistResponse(IReadOnlyList<MigrationCheck> Checks);

public sealed record MigrationCheck(string Name, bool Passed, string Detail);

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

public sealed record UpdateManifest(
    string NodeVersion,
    string ProtocolVersion,
    string IrohSidecarVersion);

public sealed record UpdateCheckResponse(
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
    string Warning)
{
    public static UpdateCheckResponse NotConfigured(NodeVersionOptions current) => new(
        false,
        current.NodeVersion,
        "",
        current.ProtocolVersion,
        "",
        current.IrohSidecarVersion,
        "",
        false,
        true,
        true,
        "",
        "Update manifest URL is not configured.");

    public static UpdateCheckResponse Failed(NodeVersionOptions current, string warning) => new(
        true,
        current.NodeVersion,
        "",
        current.ProtocolVersion,
        "",
        current.IrohSidecarVersion,
        "",
        false,
        false,
        false,
        current.UpdateManifestUrl,
        warning);
}
