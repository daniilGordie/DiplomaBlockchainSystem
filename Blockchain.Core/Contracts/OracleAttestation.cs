using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Blockchain.Core.Contracts;

public static class OracleAttestation
{
    public const string PublicKeyProperty = "OraclePublicKey";
    public const string SignatureProperty = "OracleSignature";
    public const string SignedPayloadProperty = "OracleSignedPayloadBase64";

    private static readonly string[] IdentityFields =
    [
        "Type",
        "Source",
        "ProjectId",
        "User",
        "CommitHash",
        "FileHash",
        "ArtifactId",
        "RepositoryUrl",
        "Branch"
    ];

    public static bool HasValidTrustedAttestation(string data, string trustedOraclePublicKey)
    {
        if (string.IsNullOrWhiteSpace(trustedOraclePublicKey))
        {
            return false;
        }

        try
        {
            using var currentDoc = JsonDocument.Parse(data);
            var currentRoot = currentDoc.RootElement;
            if (currentRoot.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!currentRoot.TryGetProperty(PublicKeyProperty, out var publicKeyProp) ||
                !currentRoot.TryGetProperty(SignatureProperty, out var signatureProp) ||
                !currentRoot.TryGetProperty(SignedPayloadProperty, out var signedPayloadProp))
            {
                return false;
            }

            string publicKey = publicKeyProp.GetString() ?? string.Empty;
            string signature = signatureProp.GetString() ?? string.Empty;
            string signedPayloadBase64 = signedPayloadProp.GetString() ?? string.Empty;
            if (!string.Equals(publicKey, trustedOraclePublicKey, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(signature) ||
                string.IsNullOrWhiteSpace(signedPayloadBase64))
            {
                return false;
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            bool signatureValid = ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(signedPayloadBase64),
                Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!signatureValid)
            {
                return false;
            }

            string signedPayloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(signedPayloadBase64));
            using var signedDoc = JsonDocument.Parse(signedPayloadJson);
            var signedRoot = signedDoc.RootElement;
            if (signedRoot.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (string field in IdentityFields)
            {
                if (!JsonFieldEquals(currentRoot, signedRoot, field))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool JsonFieldEquals(JsonElement left, JsonElement right, string field)
    {
        bool leftHas = left.TryGetProperty(field, out var leftValue);
        bool rightHas = right.TryGetProperty(field, out var rightValue);
        if (!leftHas && !rightHas)
        {
            return true;
        }

        if (leftHas != rightHas)
        {
            return false;
        }

        return leftValue.GetRawText() == rightValue.GetRawText();
    }
}
