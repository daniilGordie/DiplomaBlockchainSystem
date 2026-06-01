namespace Blockchain.UI.Application.Clients;

public sealed record PeerDirectorySnapshot(
    string CurrentNodeId,
    string CurrentPublicUrl,
    string CurrentRole,
    IReadOnlyList<PeerNodeInfo> Peers);
