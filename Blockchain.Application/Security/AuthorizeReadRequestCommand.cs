namespace Blockchain.Application.Security;

public sealed record AuthorizeReadRequestCommand(
    string Scope,
    string UserName,
    string UserPublicKey,
    string AuthSignature,
    string AuthTimestamp,
    string AuthNonce);
