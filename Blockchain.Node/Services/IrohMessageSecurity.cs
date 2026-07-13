using System.Security.Cryptography;
using System.Text;
using Blockchain.Application.Git;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

public sealed class IrohMessageSecurity
{
    private const string MessageVersion = "NEXUS_IROH_MESSAGE_V1";
    private readonly NodeIdentity _nodeIdentity;
    private readonly IRequestReplayGuard _replayGuard;
    private readonly P2POptions _options;
    private readonly ILogger<IrohMessageSecurity> _logger;

    public IrohMessageSecurity(
        NodeIdentity nodeIdentity,
        IRequestReplayGuard replayGuard,
        IOptions<P2POptions> options,
        ILogger<IrohMessageSecurity> logger)
    {
        _nodeIdentity = nodeIdentity;
        _replayGuard = replayGuard;
        _options = options.Value;
        _logger = logger;
    }

    public IrohMessageAuth CreateAuth(string operation, string payloadHash)
    {
        string signedAt = DateTimeOffset.UtcNow.ToString("O");
        string nonce = Guid.NewGuid().ToString("N");
        string payload = BuildPayload(operation, payloadHash, _options.EffectiveNodeId, _nodeIdentity.PublicKey, signedAt, nonce);
        return new IrohMessageAuth(
            _options.EffectiveNodeId,
            _nodeIdentity.PublicKey,
            _nodeIdentity.SignPayload(payload),
            signedAt,
            nonce);
    }

    public IrohMessageSecurityResult Validate(IrohMessageAuth? auth, string operation, string payloadHash)
    {
        if (auth == null ||
            string.IsNullOrWhiteSpace(auth.NodeId) ||
            string.IsNullOrWhiteSpace(auth.NodePublicKey) ||
            string.IsNullOrWhiteSpace(auth.Signature) ||
            string.IsNullOrWhiteSpace(auth.SignedAt) ||
            string.IsNullOrWhiteSpace(auth.Nonce))
        {
            return IrohMessageSecurityResult.Reject("missing iroh message auth");
        }

        if (!DateTimeOffset.TryParse(auth.SignedAt, out var signedAt))
        {
            return IrohMessageSecurityResult.Reject("invalid iroh message timestamp");
        }

        int skewSeconds = Math.Clamp(_options.RegistrationClockSkewSeconds, 30, 3600);
        var now = DateTimeOffset.UtcNow;
        if (signedAt < now.AddSeconds(-skewSeconds) || signedAt > now.AddSeconds(skewSeconds))
        {
            return IrohMessageSecurityResult.Reject("iroh message timestamp is outside allowed clock skew");
        }

        try
        {
            byte[] publicKey = Convert.FromBase64String(auth.NodePublicKey);
            byte[] signature = Convert.FromBase64String(auth.Signature);
            string payload = BuildPayload(operation, payloadHash, auth.NodeId, auth.NodePublicKey, auth.SignedAt, auth.Nonce);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            bool valid = ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(payload),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            if (!valid)
            {
                return IrohMessageSecurityResult.Reject("invalid iroh message signature");
            }

            string replayKey = $"iroh:{auth.NodePublicKey}:{operation}:{auth.Nonce}";
            if (!_replayGuard.TryRegister(replayKey, now.UtcDateTime))
            {
                return IrohMessageSecurityResult.Reject("iroh message nonce was already used");
            }

            return IrohMessageSecurityResult.Accept(auth.NodeId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[IrohSecurity] Failed to verify message signature: {Message}", ex.Message);
            return IrohMessageSecurityResult.Reject("invalid iroh message public key or signature");
        }
    }

    public static string HashPayload(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildPayload(
        string operation,
        string payloadHash,
        string nodeId,
        string nodePublicKey,
        string signedAt,
        string nonce) =>
        string.Join('\n', new[]
        {
            MessageVersion,
            operation.Trim(),
            payloadHash.Trim(),
            nodeId.Trim(),
            nodePublicKey.Trim(),
            signedAt.Trim(),
            nonce.Trim()
        });
}

public sealed record IrohMessageAuth(
    string NodeId,
    string NodePublicKey,
    string Signature,
    string SignedAt,
    string Nonce);

public sealed record IrohMessageSecurityResult(
    bool Accepted,
    string Message,
    string NodeId)
{
    public static IrohMessageSecurityResult Accept(string nodeId) => new(true, "accepted", nodeId);
    public static IrohMessageSecurityResult Reject(string message) => new(false, message, string.Empty);
}
