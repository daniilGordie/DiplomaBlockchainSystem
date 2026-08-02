using Blockchain.UI.Application.Clients;
using Blockchain.UI.Application.State;
using Microsoft.Extensions.Configuration;

namespace Blockchain.UI.Application.UseCases;

public sealed class DashboardActions
{
    private readonly IConfiguration _configuration;
    private readonly IPeerNetworkClient _peerNetworkClient;

    public DashboardActions(
        IConfiguration configuration,
        IPeerNetworkClient peerNetworkClient)
    {
        _configuration = configuration;
        _peerNetworkClient = peerNetworkClient;
    }

    public string GetInitialNodeUrl()
    {
        return NormalizeNodeUrl(_configuration["NodeUrl"] ?? "https://localhost:7066");
    }

    public UiResult<string> ValidateNodeUrl(string url)
    {
        string normalized = NormalizeNodeUrl(url);
        if (!IsHttpNodeUrl(normalized))
        {
            return UiResult<string>.Fail("Enter a valid HTTP or HTTPS node URL.");
        }

        return UiResult<string>.Ok(normalized);
    }

    public async Task<UiResult<PeerNetworkOverview>> LoadPeersAsync(string nodeUrl)
    {
        try
        {
            var directory = await _peerNetworkClient.GetPeerDirectoryAsync(nodeUrl);
            var setupStatus = await TryLoadAsync("setup status", () => _peerNetworkClient.GetSetupStatusAsync(nodeUrl));
            var networkStatus = await TryLoadAsync("network status", () => _peerNetworkClient.GetNetworkStatusAsync(nodeUrl));
            var networkInvite = await TryLoadAsync("network invite", () => _peerNetworkClient.GetNetworkInviteAsync(nodeUrl));
            var intentList = await TryLoadAsync("intent list", () => _peerNetworkClient.GetIntentListAsync(nodeUrl));
            var migrationChecklist = await TryLoadAsync("migration checklist", () => _peerNetworkClient.GetMigrationChecklistAsync(nodeUrl));
            var updateCheck = await TryLoadAsync("update check", () => _peerNetworkClient.GetUpdateCheckAsync(nodeUrl));

            return UiResult<PeerNetworkOverview>.Ok(new PeerNetworkOverview(directory, setupStatus, networkStatus, networkInvite, intentList, migrationChecklist, updateCheck));
        }
        catch (Exception ex)
        {
            return UiResult<PeerNetworkOverview>.Fail($"Node unavailable: {ex.Message}");
        }
    }

    public Task<SetupPlanResponse?> CreateSetupPlanAsync(string nodeUrl, SetupPlanRequest request) =>
        _peerNetworkClient.CreateSetupPlanAsync(nodeUrl, request);

    public Task<PeerTrustResponse?> SetPeerTrustAsync(string nodeUrl, PeerTrustRequest request) =>
        _peerNetworkClient.SetPeerTrustAsync(nodeUrl, request);

    public Task<PeerRoleResponse?> SetPeerRoleAsync(string nodeUrl, PeerRoleRequest request) =>
        _peerNetworkClient.SetPeerRoleAsync(nodeUrl, request);

    public Task<EdgeSyncStatus?> SyncNetworkAsync(string nodeUrl, NetworkSyncRequest request) =>
        _peerNetworkClient.SyncNetworkAsync(nodeUrl, request);

    private static async Task<T?> TryLoadAsync<T>(string resource, Func<Task<T?>> loader)
    {
        try
        {
            return await loader();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load {resource}: {ex.Message}");
            return default;
        }
    }

    private static string NormalizeNodeUrl(string url) => url.Trim().TrimEnd('/');

    private static bool IsHttpNodeUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
