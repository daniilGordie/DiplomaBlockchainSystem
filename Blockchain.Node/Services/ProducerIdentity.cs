using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Blockchain.Node.Services;

public sealed class ProducerIdentity
{
    private readonly ECDsa? _ecdsa;

    public bool IsConfigured { get; }
    public string PublicKey { get; }

    public ProducerIdentity(IConfiguration config)
    {
        string keyFileName = config["Consensus:ProducerKeyPath"] ?? string.Empty;
        string keyPassword = config["Consensus:ProducerPrivateKeyPassword"]
            ?? config["NodeDbPassword"]
            ?? string.Empty;

        if (string.IsNullOrWhiteSpace(keyFileName))
        {
            PublicKey = string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(keyPassword))
        {
            throw new InvalidOperationException("Consensus:ProducerPrivateKeyPassword (or NodeDbPassword fallback) is required when Consensus:ProducerKeyPath is configured.");
        }

        _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string? keyDirectory = Path.GetDirectoryName(keyFileName);
        if (!string.IsNullOrWhiteSpace(keyDirectory))
        {
            Directory.CreateDirectory(keyDirectory);
        }

        if (File.Exists(keyFileName))
        {
            byte[] encryptedBytes = File.ReadAllBytes(keyFileName);
            _ecdsa.ImportEncryptedPkcs8PrivateKey(
                Encoding.UTF8.GetBytes(keyPassword),
                encryptedBytes,
                out _);
        }
        else
        {
            byte[] encryptedBytes = _ecdsa.ExportEncryptedPkcs8PrivateKey(
                Encoding.UTF8.GetBytes(keyPassword),
                new PbeParameters(
                    PbeEncryptionAlgorithm.Aes256Cbc,
                    HashAlgorithmName.SHA256,
                    100000));

            File.WriteAllBytes(keyFileName, encryptedBytes);
        }

        PublicKey = Convert.ToBase64String(_ecdsa.ExportSubjectPublicKeyInfo());
        IsConfigured = true;
    }

    public string SignData(string data)
    {
        if (_ecdsa == null)
        {
            throw new InvalidOperationException("Producer identity is not configured.");
        }

        byte[] signature = _ecdsa.SignData(
            Encoding.UTF8.GetBytes(data),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }
}
