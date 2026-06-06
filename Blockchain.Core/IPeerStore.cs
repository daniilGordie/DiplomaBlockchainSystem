namespace Blockchain.Core;

public interface IPeerStore
{
    List<PeerInfo> LoadPeerInfos();
    List<string> LoadPeers();
    void SavePeer(PeerInfo peer);
    void SavePeer(string url);
    void MarkPeerSeen(string url);
    void MarkPeerFailure(string url);
}

public sealed record PeerInfo(
    string Url,
    string NodeId,
    string Role,
    string? LastSeen = null,
    string? LastFailure = null,
    bool IsTrusted = true,
    string NodePublicKey = "")
{
    public static PeerInfo FromUrl(string url, string role = "Full") => new(url, string.Empty, role);
}
