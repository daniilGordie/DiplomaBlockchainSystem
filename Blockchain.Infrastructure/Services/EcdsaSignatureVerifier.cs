using System.Security.Cryptography;
using System.Text;
using Blockchain.Application.Git;

namespace Blockchain.Infrastructure.Services;

public sealed class EcdsaSignatureVerifier : ISignatureVerifier
{
    public bool Verify(string data, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            byte[] signatureBytes = Convert.FromBase64String(signatureBase64);
            if (signatureBytes.Length != 64)
            {
                return false;
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(data), signatureBytes, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}
