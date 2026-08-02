using Blockchain.Core;
using Blockchain.Node.Services;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
                !string.IsNullOrWhiteSpace(options.EffectiveIdentityKeyPath),
                Fingerprint(nodeIdentity.PublicKey),
                options.AllowRegistrationTokenFallback,
                options.Iroh.Enabled,
                options.Iroh.NormalizedSidecarUrl,
                irohStatus != null,
                irohStatus?.NodeId ?? string.Empty,
                irohStatus?.RelayUrl ?? string.Empty,
                irohStatus?.ConnectionPath ?? string.Empty,
                irohStatus?.TransportMode ?? string.Empty,
                irohStatus?.DirectAddresses?.Length ?? 0,
                Math.Clamp(options.DiscoveryIntervalSeconds, 10, 3600),
                versionOptions.Value.NodeVersion,
                versionOptions.Value.ProtocolVersion));
        });

        endpoints.MapGet("/api/node/status", async (
            IConfiguration configuration,
            IOptions<NexusNodeOptions> node,
            IOptions<P2POptions> p2p,
            IOptions<RaftOptions> raft,
            NodeIdentity nodeIdentity,
            IrohSidecarClient irohSidecar) =>
        {
            var nodeValue = node.Value;
            var p2pValue = p2p.Value;
            var raftValue = raft.Value;
            var irohStatus = p2pValue.Iroh.Enabled
                ? await irohSidecar.GetStatusAsync()
                : null;
            var diagnostics = BuildNodeDiagnostics(configuration, nodeValue, p2pValue, raftValue);

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
                irohStatus?.ConnectionPath ?? string.Empty,
                irohStatus?.TransportMode ?? string.Empty,
                irohStatus?.DirectAddresses?.Length ?? 0,
                ConsensusPolicy.GetFinalityMode(nodeValue),
                ConsensusPolicy.GetEngineName(nodeValue),
                nodeValue.IsConsensusMember,
                raftValue.Transport,
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
            IOptions<RaftOptions> raft,
            NodeIdentity nodeIdentity,
            IrohSidecarClient irohSidecar,
            EdgeCommittedBlockSyncService edgeSync,
            IrohProposalForwarder proposalForwarder,
            IPeerStore peerStore,
            IBlockStore blockStore) =>
        {
            var nodeValue = node.Value;
            var p2pValue = p2p.Value;
            var raftValue = raft.Value;
            var irohStatus = p2pValue.Iroh.Enabled
                ? await irohSidecar.GetStatusAsync()
                : null;
            var diagnostics = BuildNodeDiagnostics(configuration, nodeValue, p2pValue, raftValue);
            var peers = peerStore.LoadAllPeerInfos()
                .Select(peer => new NetworkPeerResponse(
                    peer.Url,
                    peer.NodeId,
                    peer.Role,
                    IrohSidecarClient.IsIrohPeerUrl(peer.Url) ? "Iroh" : "HttpGrpc",
                    peer.LastSeen ?? string.Empty,
                    peer.LastFailure ?? string.Empty,
                    peer.IsTrusted,
                    peer.NetworkId,
                    peer.PublicKeyFingerprint,
                    peer.IrohNodeId,
                    peer.RequestedRole,
                    peer.MembershipStatus,
                    peer.ApprovedAt,
                    peer.ApprovedBy,
                    peer.RejectedAt,
                    peer.RejectedBy,
                    peer.RevokedAt,
                    peer.RevokedBy,
                    peer.Reason,
                    peer.AppVersion,
                    peer.ProtocolVersion,
                    peer.Capabilities))
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
                FirstNonEmpty(configuration["Network:Id"], configuration["NetworkId"], "nexus-main"),
                configuration["Network:TrustedBootstrapFingerprint"] ?? string.Empty,
                p2pValue.Iroh.Enabled,
                irohStatus != null,
                irohStatus?.PublicUrl ?? string.Empty,
                irohStatus?.ConnectionPath ?? string.Empty,
                irohStatus?.TransportMode ?? string.Empty,
                irohStatus?.DirectAddresses?.Length ?? 0,
                p2pValue.NormalizedBootstrapPeers.ToArray(),
                peers,
                ConsensusPolicy.GetFinalityMode(nodeValue),
                ConsensusPolicy.GetEngineName(nodeValue),
                nodeValue.IsConsensusMember,
                raftValue.Transport,
                raftValue.HasMinimumConfiguration,
                raftValue.NodeId,
                raftValue.PublicEndPoint,
                channels,
                ToEdgeSyncResponse(edgeSync.GetStatus()),
                ToEdgeProposalForwardingResponse(proposalForwarder.GetStatus()),
                diagnostics.Errors,
                diagnostics.Warnings));
        });

        endpoints.MapGet("/api/network/peers", (
            IPeerStore peerStore) =>
        {
            var peers = peerStore.LoadAllPeerInfos()
                .Select(peer => new NetworkPeerResponse(
                    peer.Url,
                    peer.NodeId,
                    peer.Role,
                    IrohSidecarClient.IsIrohPeerUrl(peer.Url) ? "Iroh" : "HttpGrpc",
                    peer.LastSeen ?? string.Empty,
                    peer.LastFailure ?? string.Empty,
                    peer.IsTrusted))
                .ToArray();

            return Results.Json(new NetworkPeersResponse(peers));
        });

        endpoints.MapGet("/api/network/invite", async (
            HttpContext httpContext,
            IConfiguration configuration,
            IOptions<NexusNodeOptions> node,
            IOptions<P2POptions> p2p,
            NodeIdentity nodeIdentity,
            IrohSidecarClient irohSidecar) =>
        {
            var p2pValue = p2p.Value;
            string fallbackHttpUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            string bootstrapHttpUrl = FirstNonEmpty(
                p2pValue.NormalizedPublicUrl,
                configuration["Network:BootstrapHttpUrl"],
                configuration["BootstrapHttpUrl"],
                fallbackHttpUrl);
            string bootstrapGrpcUrl = FirstNonEmpty(
                configuration["Network:BootstrapGrpcUrl"],
                configuration["BootstrapGrpcUrl"],
                bootstrapHttpUrl);
            string irohUrl = string.Empty;
            if (p2pValue.Iroh.Enabled)
            {
                irohUrl = await irohSidecar.GetPublicUrlAsync();
            }

            var invite = new NetworkInviteResponse(
                FirstNonEmpty(configuration["Network:Id"], configuration["NetworkId"], "nexus-main"),
                bootstrapHttpUrl,
                bootstrapGrpcUrl,
                irohUrl,
                p2pValue.EffectiveNodeId,
                node.Value.EffectiveRole.ToString(),
                Fingerprint(nodeIdentity.PublicKey),
                "Edge",
                DateTime.UtcNow,
                string.Empty);
            invite = invite with { Token = EncodeInvite(invite) };
            return Results.Json(invite);
        });

        endpoints.MapPost("/api/network/peers/trust", (
            PeerTrustRequest request,
            IConfiguration configuration,
            IPeerStore peerStore,
            P2PNetworkService p2pNetwork) =>
        {
            if (!IsValidAdminToken(configuration, request.AdminToken))
            {
                return Results.Unauthorized();
            }

            string url = P2POptions.NormalizeUrl(request.Url);
            if (string.IsNullOrWhiteSpace(url))
            {
                return Results.BadRequest(new PeerTrustResponse(false, "Peer URL is required."));
            }

            peerStore.SetPeerTrust(url, request.IsTrusted);
            if (request.IsTrusted)
            {
                p2pNetwork.AddPeer(url);
            }
            else
            {
                p2pNetwork.RemovePeer(url);
            }

            return Results.Json(new PeerTrustResponse(true, request.IsTrusted ? "Peer approved." : "Peer revoked."));
        });

        endpoints.MapPost("/api/network/peers/role", (
            PeerRoleRequest request,
            IConfiguration configuration,
            IPeerStore peerStore,
            P2PNetworkService p2pNetwork) =>
        {
            if (!IsValidAdminToken(configuration, request.AdminToken))
            {
                return Results.Unauthorized();
            }

            string url = P2POptions.NormalizeUrl(request.Url);
            if (string.IsNullOrWhiteSpace(url))
            {
                return Results.BadRequest(new PeerRoleResponse(false, "Peer URL is required.", string.Empty));
            }

            string role = NormalizePeerRole(request.Role);
            if (string.IsNullOrWhiteSpace(role))
            {
                return Results.BadRequest(new PeerRoleResponse(false, "Peer role must be Edge, Consensus, Bootstrap, or Full.", string.Empty));
            }

            peerStore.SetPeerRole(url, role);
            var peer = peerStore.LoadAllPeerInfos()
                .FirstOrDefault(peer => string.Equals(peer.Url, url, StringComparison.OrdinalIgnoreCase));
            if (peer?.IsTrusted == true)
            {
                p2pNetwork.AddPeer(url);
            }

            return Results.Json(new PeerRoleResponse(true, $"Peer role updated to {role}.", role));
        });

        endpoints.MapPost("/api/network/membership/join", (
            MembershipJoinRequest request,
            IConfiguration configuration,
            IPeerStore peerStore) =>
        {
            string networkId = FirstNonEmpty(configuration["Network:Id"], configuration["NetworkId"], "nexus-main");
            if (!string.Equals(request.NetworkId, networkId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new MembershipActionResponse(false, "Node network id is incompatible with this network."));
            }

            string url = P2POptions.NormalizeUrl(FirstNonEmpty(request.IrohUrl, request.PublicEndpoint));
            if (string.IsNullOrWhiteSpace(url))
            {
                return Results.BadRequest(new MembershipActionResponse(false, "Join request requires an Iroh URL or public endpoint."));
            }

            string role = NormalizePeerRole(FirstNonEmpty(request.RequestedRole, "Edge"));
            if (string.IsNullOrWhiteSpace(role))
            {
                return Results.BadRequest(new MembershipActionResponse(false, "Requested role must be Edge, Consensus, Bootstrap, or Full."));
            }

            var peer = new PeerInfo(
                url,
                request.NodeId,
                "Edge",
                DateTime.UtcNow.ToString("O"),
                null,
                false,
                request.NodePublicKey,
                request.NetworkId,
                Fingerprint(request.NodePublicKey),
                request.IrohNodeId,
                role,
                PeerMembershipStatuses.PendingApproval,
                AppVersion: request.AppVersion,
                ProtocolVersion: request.ProtocolVersion,
                Capabilities: NormalizeCapabilities(request.Capabilities));
            peerStore.SavePeer(peer);
            peerStore.SetPeerMembership(url, PeerMembershipStatuses.PendingApproval, request.NodeId, "join request submitted");
            return Results.Json(new MembershipActionResponse(true, "Join request is awaiting administrator approval."));
        });

        endpoints.MapGet("/api/network/membership", (
            IPeerStore peerStore,
            string? status) =>
        {
            var peers = peerStore.LoadAllPeerInfos()
                .Where(peer => string.IsNullOrWhiteSpace(status) || string.Equals(peer.MembershipStatus, status, StringComparison.OrdinalIgnoreCase))
                .Select(ToMembershipResponse)
                .ToArray();
            return Results.Json(new MembershipListResponse(peers));
        });

        endpoints.MapGet("/api/network/membership/{nodeIdOrUrl}", (
            string nodeIdOrUrl,
            IPeerStore peerStore) =>
        {
            var peer = peerStore.LoadAllPeerInfos()
                .FirstOrDefault(item =>
                    string.Equals(item.NodeId, nodeIdOrUrl, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Url, nodeIdOrUrl, StringComparison.OrdinalIgnoreCase));
            return peer == null
                ? Results.NotFound(new MembershipActionResponse(false, "Node not found."))
                : Results.Json(ToMembershipResponse(peer));
        });

        endpoints.MapPost("/api/network/membership/{nodeIdOrUrl}/status", (
            string nodeIdOrUrl,
            MembershipStatusChangeRequest request,
            IConfiguration configuration,
            IPeerStore peerStore,
            P2PNetworkService p2pNetwork) =>
        {
            if (!IsValidAdminToken(configuration, request.AdminToken))
            {
                return Results.Unauthorized();
            }

            var peer = FindPeer(peerStore, nodeIdOrUrl);
            if (peer == null)
            {
                return Results.NotFound(new MembershipActionResponse(false, "Node not found."));
            }

            string status = NormalizeMembershipStatus(request.Status);
            peerStore.SetPeerMembership(peer.Url, status, request.Actor, request.Reason);
            if (status == PeerMembershipStatuses.Approved || status == PeerMembershipStatuses.ConsensusCandidate)
            {
                p2pNetwork.AddPeer(peer.Url);
            }
            else if (status == PeerMembershipStatuses.Revoked || status == PeerMembershipStatuses.Rejected)
            {
                p2pNetwork.RemovePeer(peer.Url);
            }

            return Results.Json(new MembershipActionResponse(true, $"Membership status updated to {status}."));
        });

        endpoints.MapPost("/api/network/membership/{nodeIdOrUrl}/capabilities", (
            string nodeIdOrUrl,
            MembershipCapabilitiesRequest request,
            IConfiguration configuration,
            IPeerStore peerStore) =>
        {
            if (!IsValidAdminToken(configuration, request.AdminToken))
            {
                return Results.Unauthorized();
            }

            var peer = FindPeer(peerStore, nodeIdOrUrl);
            if (peer == null)
            {
                return Results.NotFound(new MembershipActionResponse(false, "Node not found."));
            }

            peerStore.SetPeerCapabilities(peer.Url, NormalizeCapabilities(request.Capabilities), request.Actor, request.Reason);
            return Results.Json(new MembershipActionResponse(true, "Capabilities updated."));
        });

        endpoints.MapPost("/api/network/membership/{nodeIdOrUrl}/promotion", (
            string nodeIdOrUrl,
            MembershipPromotionRequest request,
            IConfiguration configuration,
            IPeerStore peerStore) =>
        {
            if (!IsValidAdminToken(configuration, request.AdminToken))
            {
                return Results.Unauthorized();
            }

            var peer = FindPeer(peerStore, nodeIdOrUrl);
            if (peer == null)
            {
                return Results.NotFound(new MembershipActionResponse(false, "Node not found."));
            }

            if (string.IsNullOrWhiteSpace(request.PublicConsensusEndpoint))
            {
                return Results.BadRequest(new MembershipActionResponse(false, "Promotion requires a public consensus endpoint. The node was not added to Raft."));
            }

            var capabilities = new HashSet<string>(request.Capabilities ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase)
            {
                "Consensus"
            };
            peerStore.SetPeerRole(peer.Url, "Consensus");
            peerStore.SetPeerMembership(peer.Url, PeerMembershipStatuses.ConsensusCandidate, request.Actor, "promotion requested; operator must apply generated consensus config");
            peerStore.SetPeerCapabilities(peer.Url, NormalizeCapabilities(capabilities), request.Actor, request.Reason);
            return Results.Json(new MembershipActionResponse(true, "Node marked as ConsensusCandidate. Apply generated consensus config before treating it as a Raft voter."));
        });

        endpoints.MapPost("/api/network/membership/{nodeIdOrUrl}/demote", (
            string nodeIdOrUrl,
            MembershipStatusChangeRequest request,
            IConfiguration configuration,
            IPeerStore peerStore) =>
        {
            if (!IsValidAdminToken(configuration, request.AdminToken))
            {
                return Results.Unauthorized();
            }

            var peer = FindPeer(peerStore, nodeIdOrUrl);
            if (peer == null)
            {
                return Results.NotFound(new MembershipActionResponse(false, "Node not found."));
            }

            peerStore.SetPeerRole(peer.Url, "Edge");
            peerStore.SetPeerMembership(peer.Url, PeerMembershipStatuses.Approved, request.Actor, "demoted to Edge");
            return Results.Json(new MembershipActionResponse(true, "Node demoted to Edge."));
        });

        endpoints.MapGet("/api/network/membership/{nodeIdOrUrl}/audit", (
            string nodeIdOrUrl,
            IPeerStore peerStore,
            int? limit) =>
        {
            var peer = FindPeer(peerStore, nodeIdOrUrl);
            if (peer == null)
            {
                return Results.NotFound(new MembershipActionResponse(false, "Node not found."));
            }

            return Results.Json(new MembershipAuditResponse(peerStore.LoadPeerAudit(peer.Url, Math.Clamp(limit ?? 100, 1, 500))));
        });

        endpoints.MapPost("/api/network/sync", async (
            NetworkSyncRequest request,
            IConfiguration configuration,
            EdgeCommittedBlockSyncService edgeSync,
            CancellationToken cancellationToken) =>
        {
            if (!IsValidAdminToken(configuration, request.AdminToken))
            {
                return Results.Unauthorized();
            }

            var status = await edgeSync.SyncOnceAsync(cancellationToken);
            return Results.Json(ToEdgeSyncResponse(status));
        });

        endpoints.MapGet("/api/setup/diagnostics", (
            IConfiguration configuration,
            IOptions<NexusNodeOptions> node,
            IOptions<P2POptions> p2p,
            IOptions<RaftOptions> raft) =>
        {
            var diagnostics = BuildNodeDiagnostics(configuration, node.Value, p2p.Value, raft.Value);
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
        var invite = ParseInvite(request.ConnectionInvite);
        string bootstrapGrpcUrl = ResolveBootstrapGrpcUrl(request, invite);
        if (mode is not ("local" or "edge" or "consensus" or "bootstrap"))
        {
            errors.Add("Choose Local node, Join network, Consensus node, or Bootstrap node.");
        }

        if (mode == "bootstrap" && !IsHttpUrl(request.PublicUrl))
        {
            errors.Add("Bootstrap mode requires a public HTTP or HTTPS URL.");
        }

        if ((mode == "edge" || mode == "consensus") && !IsHttpUrl(bootstrapGrpcUrl))
        {
            errors.Add("Join network mode requires a bootstrap HTTP/gRPC URL or a valid connection invite.");
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
        RaftOptions raft)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        AddValueDiagnostics(configuration, "Node:Role", errors, warnings);
        AddValueDiagnostics(configuration, "NodeDbPassword", errors, warnings);
        AddValueDiagnostics(configuration, "NodeAdminToken", errors, warnings);
        AddValueDiagnostics(configuration, "WebhookSecret", errors, warnings);
        AddValueDiagnostics(configuration, "Consensus:ProducerPrivateKeyPassword", errors, warnings);
        AddValueDiagnostics(configuration, "OraclePrivateKeyPassword", errors, warnings);
        AddValueDiagnostics(configuration, "Network:Id", errors, warnings);
        AddValueDiagnostics(configuration, "Network:BootstrapHttpUrl", errors, warnings);
        AddValueDiagnostics(configuration, "Network:BootstrapGrpcUrl", errors, warnings);
        AddValueDiagnostics(configuration, "Network:BootstrapIrohUrl", errors, warnings);
        AddValueDiagnostics(configuration, "Network:TrustedBootstrapNodeId", errors, warnings);
        AddValueDiagnostics(configuration, "Network:TrustedBootstrapFingerprint", errors, warnings);
        AddValueDiagnostics(configuration, "P2P:Iroh:LocalApiToken", errors, warnings);
        AddValueDiagnostics(configuration, "Raft:NodeId", errors, warnings);
        AddValueDiagnostics(configuration, "Raft:Transport", errors, warnings);
        AddValueDiagnostics(configuration, "Raft:PublicEndPoint", errors, warnings);
        AddValueDiagnostics(configuration, "Raft:Peers:0:Id", errors, warnings);
        AddValueDiagnostics(configuration, "Raft:Peers:0:EndPoint", errors, warnings);
        if (node.IsEdge && !p2p.Iroh.Enabled)
        {
            errors.Add("Node:Role=Edge requires P2P:Iroh:Enabled=true.");
        }

        if ((node.IsEdge || node.EffectiveRole == NexusNodeRole.Consensus) && p2p.NormalizedBootstrapPeers.Count == 0)
        {
            errors.Add("Edge/Consensus nodes require at least one P2P bootstrap peer.");
        }

        if (node.IsEdge && string.IsNullOrWhiteSpace(configuration["Network:TrustedBootstrapFingerprint"]))
        {
            warnings.Add("Edge node has no trusted bootstrap fingerprint. Use a connection invite from the bootstrap node.");
        }

        if (node.IsEdge && raft.HasMinimumConfiguration)
        {
            warnings.Add("Edge nodes ignore local Raft configuration. Use Consensus or Bootstrap role for Raft membership.");
        }

        if (node.IsConsensusMember && !raft.HasMinimumConfiguration)
        {
            errors.Add("Consensus/Bootstrap nodes require Raft:NodeId and a transport endpoint.");
        }

        if (node.IsConsensusMember && !raft.UsesSupportedTransport)
        {
            errors.Add($"Raft:Transport={raft.Transport} is not supported. Use Tcp or Iroh.");
        }

        if (node.IsConsensusMember && raft.UsesIrohTransport)
        {
            if (!p2p.Iroh.Enabled)
            {
                errors.Add("Raft:Transport=Iroh requires P2P:Iroh:Enabled=true.");
            }

            if (string.IsNullOrWhiteSpace(raft.IrohNodeId))
            {
                errors.Add("Raft:Transport=Iroh requires Raft:IrohNodeId.");
            }

            if (raft.Peers.Any(peer => !string.IsNullOrWhiteSpace(peer.EndPoint) && !IrohSidecarClient.IsIrohPeerUrl(peer.EndPoint)))
            {
                errors.Add("Raft:Transport=Iroh requires all Raft peer endpoints to use iroh://<node-id>.");
            }
        }

        if (node.IsConsensusMember && raft.HasMinimumConfiguration && !raft.HasRemotePeers)
        {
            warnings.Add("Raft is configured as a single-member cluster. Blocks can be finalized, but add consensus peers for fault tolerance.");
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
        var invite = ParseInvite(request.ConnectionInvite);
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

        if (invite != null && string.IsNullOrWhiteSpace(invite.TrustedBootstrapFingerprint))
        {
            warnings.Add("Connection invite does not include a trusted bootstrap fingerprint.");
        }

        return warnings;
    }

    private static string BuildSetupEnv(string mode, SetupPlanRequest request)
    {
        string nodeId = string.IsNullOrWhiteSpace(request.NodeId) ? NewNodeId(mode) : request.NodeId.Trim();
        string relayMode = NormalizeRelayMode(request.RelayMode);
        var invite = ParseInvite(request.ConnectionInvite);
        string bootstrapGrpcUrl = ResolveBootstrapGrpcUrl(request, invite);
        var lines = new List<string>
        {
            "ASPNETCORE_ENVIRONMENT=Production",
            "NETWORK_ID=" + (invite?.NetworkId ?? (mode == "local" ? "nexus-local" : "nexus-main")),
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
            "CONSENSUS_PRODUCER_KEY_PATH=/data/producer-key.dat",
            "CONSENSUS_PRODUCER_KEY_PASSWORD=" + NewSecret(),
            "P2P_NODE_ID=" + nodeId,
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
            lines.Add("RAFT_TRANSPORT=Tcp");
            lines.Add("RAFT_NODE_ID=" + nodeId);
            lines.Add("RAFT_PUBLIC_ENDPOINT=" + DeriveHostPort(request.PublicUrl.Trim(), 6041));
            lines.Add("RAFT_LOG_PATH=/data/raft-log");
            lines.Add("RAFT_USE_PERSISTENT_MEMBERSHIP=true");
            lines.Add("RAFT_MEMBERSHIP_PATH=/data/raft-membership");
            lines.Add("RAFT_SNAPSHOT_PATH=/data/raft-snapshots");
            lines.Add("RAFT_PEER_ID=");
            lines.Add("RAFT_PEER_ENDPOINT=");
            lines.Add("P2P_PUBLIC_URL=" + request.PublicUrl.Trim());
            lines.Add("NETWORK_BOOTSTRAP_HTTP_URL=" + request.PublicUrl.Trim());
            lines.Add("NETWORK_BOOTSTRAP_GRPC_URL=" + request.PublicUrl.Trim());
            lines.Add("NETWORK_BOOTSTRAP_IROH_URL=");
            lines.Add("NETWORK_TRUSTED_BOOTSTRAP_NODE_ID=" + nodeId);
            lines.Add("NETWORK_TRUSTED_BOOTSTRAP_FINGERPRINT=");
            lines.Add("P2P_MAX_REGISTERED_PEERS=5000");
            lines.Add("P2P_MAX_REGISTRATIONS_PER_MINUTE_PER_ADDRESS=30");
        }
        else
        {
            lines.Add("NODE_HTTP_PORT=" + (mode == "local" ? "7040" : "7042"));
            lines.Add("NODE_GRPC_PORT=" + (mode == "local" ? "7140" : "7142"));
            if (mode == "local")
            {
                lines.Add("UI_HTTP_PORT=7080");
            }
            lines.Add("P2P_BOOTSTRAP_GRPC_URL=" + (mode == "local" ? "" : bootstrapGrpcUrl));
            lines.Add("NETWORK_BOOTSTRAP_HTTP_URL=" + (mode == "local" ? "" : (invite?.BootstrapHttpUrl ?? "")));
            lines.Add("NETWORK_BOOTSTRAP_GRPC_URL=" + (mode == "local" ? "" : bootstrapGrpcUrl));
            lines.Add("NETWORK_BOOTSTRAP_IROH_URL=" + (mode == "local" ? "" : (invite?.BootstrapIrohUrl ?? "")));
            lines.Add("NETWORK_TRUSTED_BOOTSTRAP_NODE_ID=" + (mode == "local" ? "" : (invite?.TrustedBootstrapNodeId ?? "")));
            lines.Add("NETWORK_TRUSTED_BOOTSTRAP_FINGERPRINT=" + (mode == "local" ? "" : (invite?.TrustedBootstrapFingerprint ?? "")));
            if (mode != "local")
            {
                lines.Add("IROH_LOCAL_API_TOKEN=" + NewSecret());
                lines.Add("IROH_SECRET_KEY_PATH=/data/iroh-secret.key");
                lines.Add("IROH_RELAY_MODE=" + relayMode);
            }

            if (mode == "consensus")
            {
                lines.Add("RAFT_PORT=6042");
                lines.Add("RAFT_TRANSPORT=Tcp");
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

    private static NetworkInviteResponse? ParseInvite(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        try
        {
            string json = trimmed.StartsWith('{')
                ? trimmed
                : Encoding.UTF8.GetString(DecodeBase64Url(trimmed));
            return JsonSerializer.Deserialize<NetworkInviteResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveBootstrapGrpcUrl(SetupPlanRequest request, NetworkInviteResponse? invite) =>
        FirstNonEmpty(invite?.BootstrapGrpcUrl, request.BootstrapGrpcUrl);

    private static string EncodeInvite(NetworkInviteResponse invite)
    {
        var payload = invite with { Token = string.Empty };
        string json = JsonSerializer.Serialize(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        int padding = padded.Length % 4;
        if (padding > 0)
        {
            padded = padded.PadRight(padded.Length + 4 - padding, '=');
        }

        return Convert.FromBase64String(padded);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string DeriveHostPort(string url, int port)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        string host = uri.Host.Contains(":", StringComparison.Ordinal) && !uri.Host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{uri.Host}]"
            : uri.Host;
        return $"{host}:{port}";
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsValidAdminToken(IConfiguration configuration, string suppliedToken)
    {
        string configuredToken = configuration["NodeAdminToken"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configuredToken) || string.IsNullOrWhiteSpace(suppliedToken))
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(configuredToken);
        byte[] actual = Encoding.UTF8.GetBytes(suppliedToken);
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(expected, actual);
    }

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

    private static string NormalizePeerRole(string? role) =>
        (role ?? "").Trim().ToLowerInvariant() switch
        {
            "bootstrap" or "bootstrap-node" => "Bootstrap",
            "consensus" or "consensus-node" or "raft" or "raft-member" => "Consensus",
            "edge" or "edge-node" or "full" or "fullnode" or "full-node" => "Edge",
            "legacy-full" => "Full",
            _ => string.Empty
        };

    private static string NormalizeMembershipStatus(string? status)
    {
        string value = (status ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "pending" or "pendingapproval" or "pending-approval" => PeerMembershipStatuses.PendingApproval,
            "approved" or "approve" => PeerMembershipStatuses.Approved,
            "rejected" or "reject" => PeerMembershipStatuses.Rejected,
            "revoked" or "revoke" => PeerMembershipStatuses.Revoked,
            "offline" => PeerMembershipStatuses.Offline,
            "stale" => PeerMembershipStatuses.Stale,
            "incompatible" => PeerMembershipStatuses.Incompatible,
            "candidate" or "consensuscandidate" or "consensus-candidate" => PeerMembershipStatuses.ConsensusCandidate,
            _ => PeerMembershipStatuses.PendingApproval
        };
    }

    private static string NormalizeCapabilities(IEnumerable<string>? capabilities)
    {
        if (capabilities == null) return "Edge";
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Edge",
            "Consensus",
            "Storage",
            "Oracle",
            "Producer",
            "Bootstrap"
        };
        var normalized = capabilities
            .Select(item => item?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => allowed.FirstOrDefault(value => string.Equals(value, item, StringComparison.OrdinalIgnoreCase)) ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return normalized.Length == 0 ? "Edge" : string.Join(",", normalized);
    }

    private static PeerInfo? FindPeer(IPeerStore peerStore, string nodeIdOrUrl) =>
        peerStore.LoadAllPeerInfos()
            .FirstOrDefault(item =>
                string.Equals(item.NodeId, nodeIdOrUrl, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Url, nodeIdOrUrl, StringComparison.OrdinalIgnoreCase));

    private static MembershipNodeResponse ToMembershipResponse(PeerInfo peer) =>
        new(
            peer.Url,
            peer.NodeId,
            peer.NetworkId,
            peer.NodePublicKey,
            peer.PublicKeyFingerprint,
            peer.IrohNodeId,
            peer.Role,
            peer.RequestedRole,
            peer.MembershipStatus,
            peer.IsTrusted,
            peer.LastSeen ?? string.Empty,
            peer.ApprovedAt,
            peer.ApprovedBy,
            peer.RejectedAt,
            peer.RejectedBy,
            peer.RevokedAt,
            peer.RevokedBy,
            peer.Reason,
            peer.AppVersion,
            peer.ProtocolVersion,
            peer.Capabilities);

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

    private static EdgeSyncStatusResponse ToEdgeSyncResponse(EdgeCommittedBlockSyncStatus status) =>
        new(
            status.Enabled,
            status.State,
            status.LastAppliedBlocks,
            status.LastChangedChannels,
            status.LastSyncUtc,
            status.LastPeer,
            status.LastError);

    private static EdgeProposalForwardingStatusResponse ToEdgeProposalForwardingResponse(EdgeProposalForwardingStatus status) =>
        new(
            status.Enabled,
            status.PendingCount,
            status.State,
            status.LastChannelId,
            status.LastPeer,
            status.LastMessage,
            status.LastAttemptUtc,
            status.SuccessCount,
            status.FailureCount);

    private static bool IsDifferent(string current, string latest) =>
        !string.IsNullOrWhiteSpace(latest)
        && !string.Equals(current, latest, StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey)) return string.Empty;
        try
        {
            byte[] raw = Convert.FromBase64String(publicKey);
            byte[] hash = System.Security.Cryptography.SHA256.HashData(raw);
            return Convert.ToHexString(hash).ToLowerInvariant()[..16];
        }
        catch
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(publicKey));
            return Convert.ToHexString(hash).ToLowerInvariant()[..16];
        }
    }
}

public sealed record SetupStatusResponse(
    string NodeId,
    string NodeRole,
    string Role,
    string PublicUrl,
    string[] BootstrapPeers,
    bool NodeIdentityConfigured,
    string NodeIdentityFingerprint,
    bool RegistrationTokenFallbackEnabled,
    bool IrohEnabled,
    string IrohSidecarUrl,
    bool IrohSidecarHealthy,
    string IrohNodeId,
    string IrohRelayUrl,
    string IrohConnectionPath,
    string IrohTransportMode,
    int IrohDirectAddressCount,
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
    string IrohConnectionPath,
    string IrohTransportMode,
    int IrohDirectAddressCount,
    string FinalityMode,
    string ConsensusEngine,
    bool RunsRaft,
    string RaftTransport,
    bool RaftConfigured,
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
    string NetworkId,
    string TrustedBootstrapFingerprint,
    bool IrohEnabled,
    bool IrohSidecarHealthy,
    string IrohPublicUrl,
    string IrohConnectionPath,
    string IrohTransportMode,
    int IrohDirectAddressCount,
    string[] BootstrapPeers,
    IReadOnlyList<NetworkPeerResponse> KnownPeers,
    string FinalityMode,
    string ConsensusEngine,
    bool RunsRaft,
    string RaftTransport,
    bool RaftConfigured,
    string RaftNodeId,
    string RaftPublicEndPoint,
    IReadOnlyList<NetworkChannelStatusResponse> Channels,
    EdgeSyncStatusResponse EdgeSync,
    EdgeProposalForwardingStatusResponse EdgeProposalForwarding,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record EdgeSyncStatusResponse(
    bool Enabled,
    string State,
    int LastAppliedBlocks,
    int LastChangedChannels,
    DateTime? LastSyncUtc,
    string LastPeer,
    string LastError);

public sealed record EdgeProposalForwardingStatusResponse(
    bool Enabled,
    int PendingCount,
    string State,
    string LastChannelId,
    string LastPeer,
    string LastMessage,
    DateTime? LastAttemptUtc,
    long SuccessCount,
    long FailureCount);

public sealed record NetworkPeerResponse(
    string Url,
    string NodeId,
    string Role,
    string Transport,
    string LastSeen,
    string LastFailure,
    bool IsTrusted,
    string NetworkId = "",
    string PublicKeyFingerprint = "",
    string IrohNodeId = "",
    string RequestedRole = "",
    string MembershipStatus = "",
    string ApprovedAt = "",
    string ApprovedBy = "",
    string RejectedAt = "",
    string RejectedBy = "",
    string RevokedAt = "",
    string RevokedBy = "",
    string Reason = "",
    string AppVersion = "",
    string ProtocolVersion = "",
    string Capabilities = "");

public sealed record NetworkPeersResponse(IReadOnlyList<NetworkPeerResponse> Peers);

public sealed record NetworkInviteResponse(
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

public sealed record MembershipJoinRequest(
    string NetworkId,
    string NodeId,
    string NodePublicKey,
    string IrohUrl,
    string IrohNodeId,
    string PublicEndpoint,
    string RequestedRole,
    string AppVersion,
    string ProtocolVersion,
    IReadOnlyList<string> Capabilities);

public sealed record MembershipStatusChangeRequest(
    string Status,
    string AdminToken,
    string Actor = "admin",
    string Reason = "");

public sealed record MembershipCapabilitiesRequest(
    IReadOnlyList<string> Capabilities,
    string AdminToken,
    string Actor = "admin",
    string Reason = "");

public sealed record MembershipPromotionRequest(
    string PublicConsensusEndpoint,
    IReadOnlyList<string> Capabilities,
    string AdminToken,
    string Actor = "admin",
    string Reason = "");

public sealed record MembershipActionResponse(
    bool Success,
    string Message);

public sealed record MembershipNodeResponse(
    string Url,
    string NodeId,
    string NetworkId,
    string NodePublicKey,
    string PublicKeyFingerprint,
    string IrohNodeId,
    string Role,
    string RequestedRole,
    string MembershipStatus,
    bool IsTrusted,
    string LastSeen,
    string ApprovedAt,
    string ApprovedBy,
    string RejectedAt,
    string RejectedBy,
    string RevokedAt,
    string RevokedBy,
    string Reason,
    string AppVersion,
    string ProtocolVersion,
    string Capabilities);

public sealed record MembershipListResponse(IReadOnlyList<MembershipNodeResponse> Nodes);

public sealed record MembershipAuditResponse(IReadOnlyList<PeerAuditEvent> Events);

public sealed record NetworkSyncRequest(string AdminToken);

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
    string OraclePublicKey,
    string ConnectionInvite = "");

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
