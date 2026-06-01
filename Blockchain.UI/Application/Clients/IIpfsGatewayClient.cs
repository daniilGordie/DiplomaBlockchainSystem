namespace Blockchain.UI.Application.Clients;

public interface IIpfsGatewayClient
{
    Task<string> GetTextAsync(string gatewayBaseUrl, string cid);
}
