using System.Net.Http.Json;
using System.Text.Json;
using Blockchain.UI.Application.Clients;
using Blockchain.UI.Models;

namespace Blockchain.UI.Infrastructure.Http;

public sealed class IpfsIntegrationClient : IIpfsIntegrationClient
{
    private readonly HttpClient _http;

    public IpfsIntegrationClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IntegrationStatusUI> GetStatusAsync(string nodeUrl)
    {
        try
        {
            using var doc = await _http.GetFromJsonAsync<JsonDocument>($"{nodeUrl}/api/integrations/ipfs/health");
            var root = doc?.RootElement;
            string apiUrl = root?.TryGetProperty("apiUrl", out var apiProp) == true ? apiProp.GetString() ?? "" : "";
            bool reachable = root?.TryGetProperty("reachable", out var reachableProp) == true && reachableProp.GetBoolean();

            return new IntegrationStatusUI
            {
                IsLoaded = true,
                IsHealthy = reachable,
                Name = "IPFS node",
                Status = reachable ? "Reachable" : "Offline",
                Endpoint = apiUrl,
                Details = reachable ? "Local IPFS API responded" : "Start the local IPFS daemon"
            };
        }
        catch (Exception ex)
        {
            return new IntegrationStatusUI
            {
                IsLoaded = true,
                IsHealthy = false,
                Name = "IPFS node",
                Status = "Unavailable",
                Endpoint = "http://127.0.0.1:5001/api/v0",
                Details = ex.Message
            };
        }
    }
}
