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

    public async Task<UpdateCheckStatus?> GetUpdateCheckAsync(string nodeUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(NormalizeNodeUrl(nodeUrl)) };
        return await http.GetFromJsonAsync<UpdateCheckStatus>("/api/setup/update-check");
    }

    private static string NormalizeNodeUrl(string nodeUrl) => nodeUrl.Trim().TrimEnd('/') + "/";
}
