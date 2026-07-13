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
            response.SyncTokenConfigured,
            response.NodeIdentityConfigured,
            response.RegistrationTokenFallbackEnabled,
            response.DiscoveryIntervalSeconds,
            peers);
    }

    public async Task<NodeSetupStatus?> GetSetupStatusAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync<NodeSetupStatus>("/api/setup/status");
    }

    public async Task<NetworkStatus?> GetNetworkStatusAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync<NetworkStatus>("/api/network/status");
    }

    public async Task<NetworkInvite?> GetNetworkInviteAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync<NetworkInvite>("/api/network/invite");
    }

    public async Task<IntentListStatus?> GetIntentListAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync<IntentListStatus>("/api/network/intents?limit=25");
    }

    public async Task<MigrationChecklist?> GetMigrationChecklistAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync<MigrationChecklist>("/api/setup/migrations");
    }

    public async Task<SetupPlanResponse?> CreateSetupPlanAsync(string nodeUrl, SetupPlanRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/setup/plan", request);
        return await response.Content.ReadFromJsonAsync<SetupPlanResponse>();
    }

    public async Task<SetupStateStatus?> GetSetupStateAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync<SetupStateStatus>("/api/setup/state");
    }

    public async Task<InviteValidationResponse?> ValidateInviteAsync(string nodeUrl, InviteValidationRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/setup/validate-invite", request);
        return await response.Content.ReadFromJsonAsync<InviteValidationResponse>();
    }

    public async Task<SetupApplyResponse?> CreateNetworkAsync(string nodeUrl, CreateNetworkSetupRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/setup/create-network", request);
        return await response.Content.ReadFromJsonAsync<SetupApplyResponse>();
    }

    public async Task<SetupApplyResponse?> JoinNetworkAsync(string nodeUrl, JoinNetworkSetupRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/setup/join-network", request);
        return await response.Content.ReadFromJsonAsync<SetupApplyResponse>();
    }

    public async Task<SetupApplyResponse?> CreateLocalNodeAsync(string nodeUrl, LocalNodeSetupRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/setup/local", request);
        return await response.Content.ReadFromJsonAsync<SetupApplyResponse>();
    }

    public async Task<PeerTrustResponse?> SetPeerTrustAsync(string nodeUrl, PeerTrustRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/network/peers/trust", request);
        if (!response.IsSuccessStatusCode)
        {
            return new PeerTrustResponse(false, $"Peer trust update failed with HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<PeerTrustResponse>();
    }

    public async Task<PeerRoleResponse?> SetPeerRoleAsync(string nodeUrl, PeerRoleRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/network/peers/role", request);
        if (!response.IsSuccessStatusCode)
        {
            return new PeerRoleResponse(false, $"Peer role update failed with HTTP {(int)response.StatusCode}.", string.Empty);
        }

        return await response.Content.ReadFromJsonAsync<PeerRoleResponse>();
    }

    public async Task<EdgeSyncStatus?> SyncNetworkAsync(string nodeUrl, NetworkSyncRequest request)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        var response = await http.PostAsJsonAsync("/api/network/sync", request);
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

        return await response.Content.ReadFromJsonAsync<EdgeSyncStatus>();
    }

    public async Task<UpdateCheckStatus?> GetUpdateCheckAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync<UpdateCheckStatus>("/api/setup/update-check");
    }

    private static string NormalizeNodeUrl(string nodeUrl) => nodeUrl.Trim().TrimEnd('/') + "/";
}
