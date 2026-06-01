namespace Blockchain.Application.Security;

public enum AuthorizeReadRequestStatus
{
    Authorized,
    MissingIdentity,
    InvalidTimestamp,
    ExpiredTimestamp,
    ReplayedNonce,
    InvalidSignature,
    PublicKeyMismatch
}

public sealed record AuthorizeReadRequestResult(
    AuthorizeReadRequestStatus Status,
    string Message)
{
    public bool Authorized => Status == AuthorizeReadRequestStatus.Authorized;
}
