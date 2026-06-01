using Blockchain.Node;
using Blockchain.UI.Infrastructure.Grpc;

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
            peers);
    }
}
