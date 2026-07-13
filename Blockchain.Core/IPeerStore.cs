namespace Blockchain.Core;

public interface IPeerStore
{
    List<PeerInfo> LoadAllPeerInfos();
    List<PeerInfo> LoadPeerInfos();
    List<string> LoadPeers();
    void SavePeer(PeerInfo peer);
    void SavePeer(string url);
    void SetPeerTrust(string url, bool isTrusted);
    void SetPeerRole(string url, string role);
    void SetPeerMembership(string url, string status, string actor, string reason);
    void SetPeerCapabilities(string url, string capabilities, string actor, string reason);
    List<PeerAuditEvent> LoadPeerAudit(string url, int limit = 100);
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
    string NodePublicKey = "",
    string NetworkId = "",
    string PublicKeyFingerprint = "",
    string IrohNodeId = "",
    string RequestedRole = "",
    string MembershipStatus = "Approved",
    string ApprovedAt = "",
    string ApprovedBy = "",
    string RejectedAt = "",
    string RejectedBy = "",
    string RevokedAt = "",
    string RevokedBy = "",
    string Reason = "",
    string AppVersion = "",
    string ProtocolVersion = "",
    string Capabilities = "")
{
    public static PeerInfo FromUrl(string url, string role = "Full") => new(url, string.Empty, role);
}

public sealed record PeerAuditEvent(
    string Url,
    string Actor,
    string Action,
    string OldValue,
    string NewValue,
    string Reason,
    string CorrelationId,
    string TimestampUtc);

public static class PeerMembershipStatuses
{
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Revoked = "Revoked";
    public const string Offline = "Offline";
    public const string Stale = "Stale";
    public const string Incompatible = "Incompatible";
    public const string ConsensusCandidate = "ConsensusCandidate";
}
