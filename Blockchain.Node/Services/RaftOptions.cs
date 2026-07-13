namespace Blockchain.Node.Services;

public sealed class RaftOptions
{
    public string Transport { get; set; } = "Tcp";
    public string NodeId { get; set; } = string.Empty;
    public string PublicEndPoint { get; set; } = string.Empty;
    public string IrohNodeId { get; set; } = string.Empty;
    public string IrohControlEndPoint { get; set; } = "127.0.0.1:60411";
    public string IrohNodeListenEndPoint { get; set; } = "127.0.0.1:60410";
    public string LogPath { get; set; } = "raft-log";
    public bool UsePersistentMembership { get; set; }
    public string MembershipPath { get; set; } = "raft-membership";
    public string SnapshotPath { get; set; } = "raft-snapshots";
    public RaftSnapshotOptions Snapshot { get; set; } = new();
    public int ElectionTimeoutMilliseconds { get; set; } = 1500;
    public int RequestTimeoutMilliseconds { get; set; } = 5000;
    public List<RaftPeerOptions> Peers { get; set; } = new();

    public bool HasMinimumConfiguration =>
        !string.IsNullOrWhiteSpace(NodeId) &&
        (UsesIrohTransport
            ? !string.IsNullOrWhiteSpace(IrohNodeId)
            : !string.IsNullOrWhiteSpace(PublicEndPoint));

    public bool HasRemotePeers =>
        Peers.Any(peer => !string.IsNullOrWhiteSpace(peer.EndPoint));

    public bool UsesTcpTransport =>
        string.IsNullOrWhiteSpace(Transport) ||
        string.Equals(Transport, "Tcp", StringComparison.OrdinalIgnoreCase);

    public bool UsesIrohTransport =>
        string.Equals(Transport, "Iroh", StringComparison.OrdinalIgnoreCase);

    public bool UsesSupportedTransport => UsesTcpTransport || UsesIrohTransport;
}

public sealed class RaftSnapshotOptions
{
    public bool Enabled { get; set; } = true;
    public int EntryThreshold { get; set; } = 100;
    public long SizeThresholdBytes { get; set; } = 16 * 1024 * 1024;
    public int RetainCount { get; set; } = 2;
}

public sealed class RaftPeerOptions
{
    public string Id { get; set; } = string.Empty;
    public string EndPoint { get; set; } = string.Empty;
}
