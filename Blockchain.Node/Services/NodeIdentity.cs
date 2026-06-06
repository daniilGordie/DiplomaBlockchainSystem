using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

public sealed class NodeIdentity
{
    private readonly P2POptions _options;
    private readonly ILogger<NodeIdentity> _logger;
    private readonly Lazy<ECDsa> _key;
    private readonly Lazy<string> _publicKey;

    public NodeIdentity(IOptions<P2POptions> options, ILogger<NodeIdentity> logger)
    {
        _options = options.Value;
        _logger = logger;
        _key = new Lazy<ECDsa>(LoadOrCreateKey);
        _publicKey = new Lazy<string>(() => Convert.ToBase64String(_key.Value.ExportSubjectPublicKeyInfo()));
    }

    public string PublicKey => _publicKey.Value;

    public SignedNodeRegistration CreateRegistration(string nodeId, string publicUrl, string role)
    {
        string signedAt = DateTimeOffset.UtcNow.ToString("O");
        string nonce = Guid.NewGuid().ToString("N");
        string payload = BuildRegistrationPayload(nodeId, publicUrl, role, signedAt, nonce);
        byte[] signature = _key.Value.SignData(
            Encoding.UTF8.GetBytes(payload),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return new SignedNodeRegistration(
            PublicKey,
            Convert.ToBase64String(signature),
            signedAt,
            nonce);
    }

    public static string BuildRegistrationPayload(string nodeId, string publicUrl, string role, string signedAt, string nonce) =>
        string.Join('\n', new[]
        {
            "NEXUS_NODE_REGISTRATION_V1",
            nodeId.Trim(),
            P2POptions.NormalizeUrl(publicUrl),
            string.IsNullOrWhiteSpace(role) ? P2PNodeRole.Full.ToString() : role.Trim(),
            signedAt.Trim(),
            nonce.Trim()
        });

    private ECDsa LoadOrCreateKey()
    {
        string path = _options.EffectiveIdentityKeyPath;
        if (File.Exists(path))
        {
            string stored = File.ReadAllText(path).Trim();
            byte[] privateKey = Convert.FromBase64String(stored);
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(privateKey, out _);
            return key;
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(path, Convert.ToBase64String(created.ExportPkcs8PrivateKey()));
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[P2P] Could not mark node identity key file as hidden.");
        }

        _logger.LogInformation("[P2P] Created node identity key at {Path}.", path);
        return created;
    }
}

public sealed record SignedNodeRegistration(
    string NodePublicKey,
    string Signature,
    string SignedAt,
    string Nonce);
