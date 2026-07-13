using Blockchain.Core;

namespace Blockchain.Infrastructure.Persistence;

public sealed class SqlitePeerStore : IPeerStore
{
    private readonly DatabaseManager _database;

    public SqlitePeerStore(DatabaseManager database)
    {
        _database = database;
    }

    public List<PeerInfo> LoadAllPeerInfos() => _database.LoadAllPeerInfos();
    public List<PeerInfo> LoadPeerInfos() => _database.LoadPeerInfos();
    public List<string> LoadPeers() => _database.LoadPeers();
    public void SavePeer(PeerInfo peer) => _database.SavePeer(peer);
    public void SavePeer(string url) => _database.SavePeer(url);
    public void SetPeerTrust(string url, bool isTrusted) => _database.SetPeerTrust(url, isTrusted);
    public void SetPeerRole(string url, string role) => _database.SetPeerRole(url, role);
    public void SetPeerMembership(string url, string status, string actor, string reason) => _database.SetPeerMembership(url, status, actor, reason);
    public void SetPeerCapabilities(string url, string capabilities, string actor, string reason) => _database.SetPeerCapabilities(url, capabilities, actor, reason);
    public List<PeerAuditEvent> LoadPeerAudit(string url, int limit = 100) => _database.LoadPeerAudit(url, limit);
    public void MarkPeerSeen(string url) => _database.MarkPeerSeen(url);
    public void MarkPeerFailure(string url) => _database.MarkPeerFailure(url);
}
