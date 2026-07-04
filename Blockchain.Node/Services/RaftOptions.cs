namespace Blockchain.Node.Services;

public sealed class RaftOptions
{
    public string NodeId { get; set; } = string.Empty;
    public string PublicEndPoint { get; set; } = string.Empty;
    public string LogPath { get; set; } = "raft-log";
    public bool UsePersistentMembership { get; set; }
    public string MembershipPath { get; set; } = "raft-membership";
    public string SnapshotPath { get; set; } = "raft-snapshots";
    public int ElectionTimeoutMilliseconds { get; set; } = 1500;
    public int RequestTimeoutMilliseconds { get; set; } = 5000;
    public List<RaftPeerOptions> Peers { get; set; } = new();

    public bool HasMinimumConfiguration =>
        !string.IsNullOrWhiteSpace(NodeId) &&
        !string.IsNullOrWhiteSpace(PublicEndPoint) &&
        Peers.Count > 0;
}

public sealed class RaftPeerOptions
{
    public string Id { get; set; } = string.Empty;
    public string EndPoint { get; set; } = string.Empty;
}
