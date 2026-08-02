using Blockchain.Node;
using Blockchain.UI.Infrastructure.Grpc;
using System.Net.Http.Json;

namespace Blockchain.UI.Application.Clients;

public sealed class PeerNetworkClient : IPeerNetworkClient
{
    private readonly INodeClientFactory _nodeClientFactory;

    public PeerNetworkClient(INodeClientFactory nodeClientFactory)
    {
        _nodeClientFactory = nodeClientFactory;
    }

    public async Task<PeerDirectorySnapshot> GetPeerDirectoryAsync(string nodeUrl)
    {
        var client = _nodeClientFactory.Create(nodeUrl);
        var response = await client.GetPeerDirectoryAsync(new EmptyRequest());
        var peers = response.Peers
            .Select(peer => new PeerNodeInfo(
                peer.NodeId,
                peer.PublicUrl,
                string.IsNullOrWhiteSpace(peer.Role) ? "Full" : peer.Role,
                peer.LastSeen,
                peer.LastFailure,
                peer.IsTrusted))
            .ToList();

        return new PeerDirectorySnapshot(
            response.CurrentNodeId,
            response.CurrentPublicUrl,
            string.IsNullOrWhiteSpace(response.CurrentRole) ? "Full" : response.CurrentRole,
            response.BootstrapPeers.ToArray(),
            response.IrohEnabled,
            response.IrohSidecarUrl,
            response.NodeIdentityConfigured,
            response.RegistrationTokenFallbackEnabled,
            response.DiscoveryIntervalSeconds,
            peers);
    }

    public async Task<NodeSetupStatus?> GetSetupStatusAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync("/api/setup/status", NodeApiJsonContext.Default.NodeSetupStatus);
    }

    public async Task<NetworkStatus?> GetNetworkStatusAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync("/api/network/status", NodeApiJsonContext.Default.NetworkStatus);
    }

    public async Task<NetworkInvite?> GetNetworkInviteAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync("/api/network/invite", NodeApiJsonContext.Default.NetworkInvite);
    }

    public async Task<IntentListStatus?> GetIntentListAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync("/api/network/intents?limit=25", NodeApiJsonContext.Default.IntentListStatus);
    }

    public async Task<MigrationChecklist?> GetMigrationChecklistAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync("/api/setup/migrations", NodeApiJsonContext.Default.MigrationChecklist);
    }

    public async Task<SetupPlanResponse?> CreateSetupPlanAsync(string nodeUrl, SetupPlanRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/setup/plan", request, NodeApiJsonContext.Default.SetupPlanRequest);
        return await response.Content.ReadFromJsonAsync(NodeApiJsonContext.Default.SetupPlanResponse);
    }

    public async Task<SetupStateStatus?> GetSetupStateAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync("/api/setup/state", NodeApiJsonContext.Default.SetupStateStatus);
    }

    public async Task<InviteValidationResponse?> ValidateInviteAsync(string nodeUrl, InviteValidationRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/setup/validate-invite", request, NodeApiJsonContext.Default.InviteValidationRequest);
        return await response.Content.ReadFromJsonAsync(NodeApiJsonContext.Default.InviteValidationResponse);
    }

    public async Task<SetupApplyResponse?> CreateNetworkAsync(string nodeUrl, CreateNetworkSetupRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/setup/create-network", request, NodeApiJsonContext.Default.CreateNetworkSetupRequest);
        return await response.Content.ReadFromJsonAsync(NodeApiJsonContext.Default.SetupApplyResponse);
    }

    public async Task<SetupApplyResponse?> JoinNetworkAsync(string nodeUrl, JoinNetworkSetupRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/setup/join-network", request, NodeApiJsonContext.Default.JoinNetworkSetupRequest);
        return await response.Content.ReadFromJsonAsync(NodeApiJsonContext.Default.SetupApplyResponse);
    }

    public async Task<SetupApplyResponse?> CreateLocalNodeAsync(string nodeUrl, LocalNodeSetupRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/setup/local", request, NodeApiJsonContext.Default.LocalNodeSetupRequest);
        return await response.Content.ReadFromJsonAsync(NodeApiJsonContext.Default.SetupApplyResponse);
    }

    public async Task<PeerTrustResponse?> SetPeerTrustAsync(string nodeUrl, PeerTrustRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/network/peers/trust", request, NodeApiJsonContext.Default.PeerTrustRequest);
        if (!response.IsSuccessStatusCode)
        {
            return new PeerTrustResponse(false, $"Peer trust update failed with HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync(NodeApiJsonContext.Default.PeerTrustResponse);
    }

    public async Task<PeerRoleResponse?> SetPeerRoleAsync(string nodeUrl, PeerRoleRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/network/peers/role", request, NodeApiJsonContext.Default.PeerRoleRequest);
        if (!response.IsSuccessStatusCode)
        {
            return new PeerRoleResponse(false, $"Peer role update failed with HTTP {(int)response.StatusCode}.", string.Empty);
        }

        return await response.Content.ReadFromJsonAsync(NodeApiJsonContext.Default.PeerRoleResponse);
    }

    public async Task<EdgeSyncStatus?> SyncNetworkAsync(string nodeUrl, NetworkSyncRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/network/sync", request, NodeApiJsonContext.Default.NetworkSyncRequest);
        if (!response.IsSuccessStatusCode)
        {
            return new EdgeSyncStatus(
                false,
                "error",
                0,
                0,
                DateTime.UtcNow,
                string.Empty,
                $"Network sync failed with HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync(NodeApiJsonContext.Default.EdgeSyncStatus);
    }

    public async Task<UpdateCheckStatus?> GetUpdateCheckAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync("/api/setup/update-check", NodeApiJsonContext.Default.UpdateCheckStatus);
    }

    private static string NormalizeNodeUrl(string nodeUrl) => nodeUrl.Trim().TrimEnd('/') + "/";
}
