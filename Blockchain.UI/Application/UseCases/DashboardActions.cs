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
            NodeSetupStatus? setupStatus = null;
            MigrationChecklist? migrationChecklist = null;
            UpdateCheckStatus? updateCheck = null;
            try
            {
                setupStatus = await _peerNetworkClient.GetSetupStatusAsync(nodeUrl);
                migrationChecklist = await _peerNetworkClient.GetMigrationChecklistAsync(nodeUrl);
                updateCheck = await _peerNetworkClient.GetUpdateCheckAsync(nodeUrl);
            }
            catch
            {
                
            }

            return UiResult<PeerNetworkOverview>.Ok(new PeerNetworkOverview(directory, setupStatus, migrationChecklist, updateCheck));
        }
        catch (Exception ex)
        {
            return UiResult<PeerNetworkOverview>.Fail($"Node unavailable: {ex.Message}");
        }
    }

    public Task<SetupPlanResponse?> CreateSetupPlanAsync(string nodeUrl, SetupPlanRequest request) =>
        _peerNetworkClient.CreateSetupPlanAsync(nodeUrl, request);

    private static string NormalizeNodeUrl(string url) => url.Trim().TrimEnd('/');

    private static bool IsHttpNodeUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
