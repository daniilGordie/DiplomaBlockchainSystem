using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

public sealed class PeerRegistrationSecurity
{
    private readonly P2POptions _options;
    private readonly ILogger<PeerRegistrationSecurity> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenNonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RegistrationWindow> _registrationWindows = new(StringComparer.OrdinalIgnoreCase);

    public PeerRegistrationSecurity(IOptions<P2POptions> options, ILogger<PeerRegistrationSecurity> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public PeerRegistrationValidation ValidateSignedRegistration(
        RegisterPeerRequest request,
        string publicUrl,
        string role)
    {
        if (string.IsNullOrWhiteSpace(request.NodePublicKey)
            || string.IsNullOrWhiteSpace(request.Signature)
            || string.IsNullOrWhiteSpace(request.SignedAt)
            || string.IsNullOrWhiteSpace(request.Nonce))
        {
            return PeerRegistrationValidation.Reject("Missing signed node identity fields");
        }

        if (!DateTimeOffset.TryParse(request.SignedAt, out var signedAt))
        {
            return PeerRegistrationValidation.Reject("Invalid registration timestamp");
        }

        int skewSeconds = Math.Clamp(_options.RegistrationClockSkewSeconds, 30, 3600);
        var now = DateTimeOffset.UtcNow;
        if (signedAt < now.AddSeconds(-skewSeconds) || signedAt > now.AddSeconds(skewSeconds))
        {
            return PeerRegistrationValidation.Reject("Registration timestamp is outside allowed clock skew");
        }

        string replayKey = $"{request.NodePublicKey}:{request.Nonce}";
        CleanupSeenNonces(now.AddSeconds(-skewSeconds * 2));
        if (!_seenNonces.TryAdd(replayKey, now))
        {
            return PeerRegistrationValidation.Reject("Registration nonce was already used");
        }

        try
        {
            byte[] publicKey = Convert.FromBase64String(request.NodePublicKey);
            byte[] signature = Convert.FromBase64String(request.Signature);
            string payload = NodeIdentity.BuildRegistrationPayload(
                request.NodeId,
                publicUrl,
                role,
                request.SignedAt,
                request.Nonce);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            bool valid = ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(payload),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            return valid
                ? PeerRegistrationValidation.Accept()
                : PeerRegistrationValidation.Reject("Invalid node registration signature");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Security] Failed to verify node registration signature: {Message}", ex.Message);
            return PeerRegistrationValidation.Reject("Invalid node public key or signature");
        }
    }

    public bool AllowRegistrationAttempt(string remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
        {
            remoteAddress = "unknown";
        }

        int limit = Math.Clamp(_options.MaxRegistrationsPerMinutePerAddress, 1, 1000);
        var now = DateTimeOffset.UtcNow;
        var window = _registrationWindows.AddOrUpdate(
            remoteAddress,
            _ => new RegistrationWindow(now, 1),
            (_, existing) =>
            {
                if (now - existing.StartedAt >= TimeSpan.FromMinutes(1))
                {
                    return new RegistrationWindow(now, 1);
                }

                return existing with { Count = existing.Count + 1 };
            });

        return window.Count <= limit;
    }

    private void CleanupSeenNonces(DateTimeOffset threshold)
    {
        foreach (var item in _seenNonces)
        {
            if (item.Value < threshold)
            {
                _seenNonces.TryRemove(item.Key, out _);
            }
        }
    }

    private sealed record RegistrationWindow(DateTimeOffset StartedAt, int Count);
}

public sealed record PeerRegistrationValidation(bool Accepted, string Message)
{
    public static PeerRegistrationValidation Accept() => new(true, "OK");
    public static PeerRegistrationValidation Reject(string message) => new(false, message);
}
