namespace Blockchain.UI.Application.Clients;

public sealed record PeerNodeInfo(
    string NodeId,
    string PublicUrl,
    string Role,
    string LastSeen,
    string LastFailure,
    bool IsTrusted)
{
    public string Status => string.IsNullOrWhiteSpace(LastFailure) ? "Online" : "Check";
}
