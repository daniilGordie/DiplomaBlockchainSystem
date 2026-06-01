namespace Blockchain.UI.Application.Clients;

public interface IPeerNetworkClient
{
    Task<PeerDirectorySnapshot> GetPeerDirectoryAsync(string nodeUrl);
}
