using Blockchain.UI.Application.Clients;

namespace Blockchain.UI.Infrastructure.Http;

public sealed class IpfsGatewayClient : IIpfsGatewayClient
{
    private readonly HttpClient _http;

    public IpfsGatewayClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> GetTextAsync(string gatewayBaseUrl, string cid)
    {
        string url = $"{gatewayBaseUrl.Trim().TrimEnd('/')}/{cid}";
        return await _http.GetStringAsync(url);
    }
}
