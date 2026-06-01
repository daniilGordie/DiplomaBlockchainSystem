using Blockchain.Core;

namespace Blockchain.Infrastructure.Persistence;

public sealed class SqlitePeerStore : IPeerStore
{
    private readonly DatabaseManager _database;

    public SqlitePeerStore(DatabaseManager database)
    {
        _database = database;
    }

    public List<PeerInfo> LoadPeerInfos() => _database.LoadPeerInfos();
    public List<string> LoadPeers() => _database.LoadPeers();
    public void SavePeer(PeerInfo peer) => _database.SavePeer(peer);
    public void SavePeer(string url) => _database.SavePeer(url);
    public void MarkPeerSeen(string url) => _database.MarkPeerSeen(url);
    public void MarkPeerFailure(string url) => _database.MarkPeerFailure(url);
}
