using Blockchain.UI.Models;

namespace Blockchain.UI.Application.Clients;

public interface IIpfsIntegrationClient
{
    Task<IntegrationStatusUI> GetStatusAsync(string nodeUrl);
}
