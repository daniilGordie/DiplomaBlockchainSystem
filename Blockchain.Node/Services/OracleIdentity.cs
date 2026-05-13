using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Blockchain.Node.Services
{
    public class OracleIdentity
    {
        private readonly ECDsa _ecdsa;
        private const string KeyFileName = "oracle_key.dat";
        private readonly string _keyPassword;

        public string PublicKey { get; private set; }

        public OracleIdentity(IConfiguration config)
        {
            _keyPassword = config["OraclePrivateKeyPassword"]
                ?? config["NodeDbPassword"]
                ?? throw new InvalidOperationException("OraclePrivateKeyPassword (or NodeDbPassword fallback) is not configured.");

            _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            if (File.Exists(KeyFileName))
            {
                byte[] encryptedBytes = File.ReadAllBytes(KeyFileName);
                _ecdsa.ImportEncryptedPkcs8PrivateKey(
                    Encoding.UTF8.GetBytes(_keyPassword),
                    encryptedBytes,
                    out _);
            }
            else
            {
                byte[] encryptedBytes = _ecdsa.ExportEncryptedPkcs8PrivateKey(
                    Encoding.UTF8.GetBytes(_keyPassword),
                    new PbeParameters(
                        PbeEncryptionAlgorithm.Aes256Cbc,
                        HashAlgorithmName.SHA256,
                        100000));

                File.WriteAllBytes(KeyFileName, encryptedBytes);
            }

            PublicKey = Convert.ToBase64String(_ecdsa.ExportSubjectPublicKeyInfo());
        }

        public string SignData(string data)
        {
            byte[] dataBytes = Encoding.UTF8.GetBytes(data);
            byte[] signature = _ecdsa.SignData(
                dataBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return Convert.ToBase64String(signature);
        }
    }
}
