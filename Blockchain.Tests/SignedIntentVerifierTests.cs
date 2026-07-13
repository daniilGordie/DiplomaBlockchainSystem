using System.Security.Cryptography;
using Blockchain.Core.Consensus;

public class SignedIntentVerifierTests
{
    [Fact]
    public void Validate_ShouldAcceptValidIntentSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var intent = CreateIntent(publicKey, timestamp, "{\"Type\":\"CreateProject\",\"ProjectId\":\"Alpha\"}");
        string signature = Sign(key, intent.CanonicalSignableData());
        intent = intent with
        {
            Signature = signature,
            IntentId = SignedIntent.ComputeIntentId(
                intent.NetworkId,
                intent.ProjectId,
                intent.ChannelId,
                intent.OperationType,
                intent.PayloadJson,
                intent.ActorPublicKey,
                intent.TimestampUnixSeconds,
                intent.Nonce,
                intent.CorrelationId,
                intent.SchemaVersion)
        };

        var result = new SignedIntentVerifier().Validate(intent, DateTimeOffset.FromUnixTimeSeconds(timestamp));

        Assert.True(result.Accepted);
        Assert.Equal(intent.IntentId, result.IntentId);
    }

    [Fact]
    public void Validate_ShouldRejectTamperedPayload()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var intent = CreateIntent(publicKey, timestamp, "{\"Type\":\"CreateProject\",\"ProjectId\":\"Alpha\"}");
        string signature = Sign(key, intent.CanonicalSignableData());
        intent = intent with
        {
            PayloadJson = "{\"Type\":\"CreateProject\",\"ProjectId\":\"Beta\"}",
            Signature = signature
        };

        var result = new SignedIntentVerifier().Validate(intent, DateTimeOffset.FromUnixTimeSeconds(timestamp));

        Assert.False(result.Accepted);
        Assert.Equal("invalid intent signature", result.Reason);
    }

    private static SignedIntent CreateIntent(string publicKey, long timestamp, string payload)
    {
        return new SignedIntent(
            "",
            "nexus-test",
            "Alpha",
            "System",
            "CreateProject",
            payload,
            publicKey,
            timestamp,
            "nonce-1",
            "",
            "correlation-1",
            1);
    }

    private static string Sign(ECDsa key, string data)
    {
        byte[] signature = key.SignData(
            System.Text.Encoding.UTF8.GetBytes(data),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }
}
