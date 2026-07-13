using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace Blockchain.Core.Cryptography;

public static class EcdsaSignatureVerifier
{
    public static bool VerifyP1363Sha256(string publicKeyBase64, string signatureBase64, string data)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64) ||
            string.IsNullOrWhiteSpace(signatureBase64) ||
            data == null)
        {
            return false;
        }

        byte[] signatureBytes;
        byte[] publicKeyBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signatureBase64);
            publicKeyBytes = Convert.FromBase64String(publicKeyBase64);
        }
        catch
        {
            return false;
        }

        if (signatureBytes.Length != 64)
        {
            return false;
        }

        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        if (VerifyWithDotNet(publicKeyBytes, signatureBytes, dataBytes))
        {
            return true;
        }

        return VerifyWithBouncyCastle(publicKeyBytes, signatureBytes, dataBytes);
    }

    private static bool VerifyWithDotNet(byte[] publicKeyBytes, byte[] signatureBytes, byte[] dataBytes)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
            return ecdsa.VerifyData(
                dataBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifyWithBouncyCastle(byte[] publicKeyBytes, byte[] signatureBytes, byte[] dataBytes)
    {
        try
        {
            if (PublicKeyFactory.CreateKey(publicKeyBytes) is not ECPublicKeyParameters publicKey)
            {
                return false;
            }

            byte[] hash = SHA256.HashData(dataBytes);
            var r = new BigInteger(1, signatureBytes.AsSpan(0, 32).ToArray());
            var s = new BigInteger(1, signatureBytes.AsSpan(32, 32).ToArray());

            var signer = new ECDsaSigner();
            signer.Init(false, publicKey);
            return signer.VerifySignature(hash, r, s);
        }
        catch
        {
            return false;
        }
    }
}
