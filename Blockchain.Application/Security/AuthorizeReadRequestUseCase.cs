using Blockchain.Application.Git;

namespace Blockchain.Application.Security;

public sealed class AuthorizeReadRequestUseCase
{
    private static readonly TimeSpan ReadSignatureTtl = TimeSpan.FromMinutes(5);

    private readonly IProjectMembershipReader _membershipReader;
    private readonly IRequestReplayGuard _replayGuard;
    private readonly ISignatureVerifier _signatureVerifier;
    private readonly IClock _clock;

    public AuthorizeReadRequestUseCase(
        IProjectMembershipReader membershipReader,
        IRequestReplayGuard replayGuard,
        ISignatureVerifier signatureVerifier,
        IClock clock)
    {
        _membershipReader = membershipReader;
        _replayGuard = replayGuard;
        _signatureVerifier = signatureVerifier;
        _clock = clock;
    }

    public AuthorizeReadRequestResult Execute(AuthorizeReadRequestCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.UserName) ||
            string.Equals(command.UserName, "Guest", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(command.UserPublicKey) ||
            string.IsNullOrWhiteSpace(command.AuthSignature) ||
            string.IsNullOrWhiteSpace(command.AuthTimestamp) ||
            string.IsNullOrWhiteSpace(command.AuthNonce))
        {
            return Reject(AuthorizeReadRequestStatus.MissingIdentity, "missing signed identity");
        }

        if (!DateTime.TryParse(command.AuthTimestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedTimestamp))
        {
            return Reject(AuthorizeReadRequestStatus.InvalidTimestamp, "invalid timestamp");
        }

        DateTime nowUtc = _clock.UtcNow;
        if ((nowUtc - parsedTimestamp.ToUniversalTime()).Duration() > ReadSignatureTtl)
        {
            return Reject(AuthorizeReadRequestStatus.ExpiredTimestamp, "expired timestamp");
        }

        string nonceKey = $"{command.UserName}:{command.Scope}:{command.AuthNonce}";
        if (!_replayGuard.TryRegister(nonceKey, nowUtc))
        {
            return Reject(AuthorizeReadRequestStatus.ReplayedNonce, "replayed nonce");
        }

        string signable = $"READ:{command.Scope}:{command.UserName}:{command.UserPublicKey}:{command.AuthTimestamp}:{command.AuthNonce}";
        if (!_signatureVerifier.Verify(signable, command.AuthSignature, command.UserPublicKey))
        {
            return Reject(AuthorizeReadRequestStatus.InvalidSignature, "invalid signature");
        }

        string? boundPublicKey = _membershipReader.GetUserPublicKey(command.UserName);
        if (!string.IsNullOrWhiteSpace(boundPublicKey) &&
            !string.Equals(boundPublicKey, command.UserPublicKey, StringComparison.Ordinal))
        {
            return Reject(AuthorizeReadRequestStatus.PublicKeyMismatch, "public key mismatch");
        }

        return new AuthorizeReadRequestResult(AuthorizeReadRequestStatus.Authorized, "authorized");
    }

    private static AuthorizeReadRequestResult Reject(AuthorizeReadRequestStatus status, string message)
    {
        return new AuthorizeReadRequestResult(status, message);
    }
}
