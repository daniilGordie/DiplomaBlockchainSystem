using System.Security.Cryptography;
using System.Text;
using Blockchain.Core.Cryptography;

namespace Blockchain.Core.Consensus;

public sealed record SignedIntent(
    string IntentId,
    string NetworkId,
    string ProjectId,
    string ChannelId,
    string OperationType,
    string PayloadJson,
    string ActorPublicKey,
    long TimestampUnixSeconds,
    string Nonce,
    string Signature,
    string CorrelationId,
    int SchemaVersion)
{
    public string PayloadHash => ComputeSha256(PayloadJson ?? string.Empty);

    public string CanonicalSignableData()
    {
        return string.Join(
            ":",
            "NEXUS_INTENT_V1",
            NetworkId ?? string.Empty,
            ProjectId ?? string.Empty,
            ChannelName.Normalize(ChannelId),
            OperationType ?? string.Empty,
            PayloadHash,
            ActorPublicKey ?? string.Empty,
            TimestampUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Nonce ?? string.Empty,
            CorrelationId ?? string.Empty,
            SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static string ComputeIntentId(
        string networkId,
        string projectId,
        string channelId,
        string operationType,
        string payloadJson,
        string actorPublicKey,
        long timestampUnixSeconds,
        string nonce,
        string correlationId,
        int schemaVersion)
    {
        string payloadHash = ComputeSha256(payloadJson ?? string.Empty);
        string canonical = string.Join(
            ":",
            "NEXUS_INTENT_ID_V1",
            networkId ?? string.Empty,
            projectId ?? string.Empty,
            ChannelName.Normalize(channelId),
            operationType ?? string.Empty,
            payloadHash,
            actorPublicKey ?? string.Empty,
            timestampUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            nonce ?? string.Empty,
            correlationId ?? string.Empty,
            schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return ComputeSha256(canonical);
    }

    private static string ComputeSha256(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class SignedIntentVerifier
{
    private static readonly TimeSpan DefaultClockSkew = TimeSpan.FromMinutes(10);

    public SignedIntentValidationResult Validate(SignedIntent intent, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(intent.NetworkId))
        {
            return SignedIntentValidationResult.Reject("network id is required");
        }

        if (string.IsNullOrWhiteSpace(intent.ProjectId))
        {
            return SignedIntentValidationResult.Reject("project id is required");
        }

        if (string.IsNullOrWhiteSpace(intent.ChannelId))
        {
            return SignedIntentValidationResult.Reject("channel id is required");
        }

        if (string.IsNullOrWhiteSpace(intent.PayloadJson))
        {
            return SignedIntentValidationResult.Reject("payload is required");
        }

        if (string.IsNullOrWhiteSpace(intent.OperationType))
        {
            return SignedIntentValidationResult.Reject("operation type is required");
        }

        if (string.IsNullOrWhiteSpace(intent.ActorPublicKey))
        {
            return SignedIntentValidationResult.Reject("actor public key is required");
        }

        if (string.IsNullOrWhiteSpace(intent.Nonce))
        {
            return SignedIntentValidationResult.Reject("nonce is required");
        }

        if (string.IsNullOrWhiteSpace(intent.Signature))
        {
            return SignedIntentValidationResult.Reject("signature is required");
        }

        string expectedIntentId = SignedIntent.ComputeIntentId(
            intent.NetworkId,
            intent.ProjectId,
            intent.ChannelId,
            intent.OperationType,
            intent.PayloadJson,
            intent.ActorPublicKey,
            intent.TimestampUnixSeconds,
            intent.Nonce,
            intent.CorrelationId,
            intent.SchemaVersion);
        if (!string.IsNullOrWhiteSpace(intent.IntentId) &&
            !string.Equals(intent.IntentId, expectedIntentId, StringComparison.OrdinalIgnoreCase))
        {
            return SignedIntentValidationResult.Reject("intent id mismatch");
        }

        var submittedAt = DateTimeOffset.FromUnixTimeSeconds(intent.TimestampUnixSeconds);
        var referenceNow = now ?? DateTimeOffset.UtcNow;
        if ((referenceNow - submittedAt).Duration() > DefaultClockSkew)
        {
            return SignedIntentValidationResult.Reject("intent timestamp is outside the accepted clock skew");
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(intent.Signature);
        }
        catch
        {
            return SignedIntentValidationResult.Reject("intent signature validation failed");
        }

        if (signatureBytes.Length != 64)
        {
            return SignedIntentValidationResult.Reject("intent signature has invalid length");
        }

        bool valid = EcdsaSignatureVerifier.VerifyP1363Sha256(
            intent.ActorPublicKey,
            intent.Signature,
            intent.CanonicalSignableData());
        return valid
            ? SignedIntentValidationResult.Accept(expectedIntentId)
            : SignedIntentValidationResult.Reject("invalid intent signature");
    }
}

public sealed record SignedIntentValidationResult(
    bool Accepted,
    string Reason,
    string IntentId)
{
    public static SignedIntentValidationResult Accept(string intentId) => new(true, "accepted", intentId);
    public static SignedIntentValidationResult Reject(string reason) => new(false, reason, string.Empty);
}

public enum IntentStatus
{
    Created,
    QueuedOffline,
    Submitted,
    Accepted,
    WaitingForProducer,
    Proposed,
    WaitingForConsensus,
    Committed,
    Rejected,
    Expired,
    FailedRetryable,
    FailedPermanent
}

public sealed record IntentSubmissionResult(
    bool Success,
    string IntentId,
    IntentStatus Status,
    string Message,
    string ChannelId,
    string? CommittedBlockHash = null);

public sealed record IntentOutboxRecord(
    SignedIntent Intent,
    string BlockEnvelopeJson,
    IntentStatus Status,
    int AttemptCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LastAttemptAtUtc,
    DateTime? NextAttemptAtUtc,
    string LastError,
    string Destination,
    string? CommittedBlockHash,
    long? CommittedBlockIndex = null,
    string? ProposalId = null);

public sealed record IntentStatusTransition(
    string IntentId,
    IntentStatus Status,
    DateTime ChangedAtUtc,
    string Message,
    string Destination,
    string? ProposalId = null,
    string? CommittedBlockHash = null,
    long? CommittedBlockIndex = null);

public interface IIntentStore
{
    void SaveIntent(IntentOutboxRecord record);
    IntentOutboxRecord? GetIntent(string intentId);
    List<IntentOutboxRecord> LoadRetryableIntents(DateTime nowUtc, int limit);
    List<IntentOutboxRecord> LoadRecentIntents(int limit);
    List<IntentStatusTransition> LoadIntentHistory(string intentId);
    void UpdateIntentStatus(
        string intentId,
        IntentStatus status,
        int attemptCount,
        DateTime updatedAtUtc,
        DateTime? lastAttemptAtUtc,
        DateTime? nextAttemptAtUtc,
        string lastError,
        string destination,
        string? committedBlockHash,
        long? committedBlockIndex = null,
        string? proposalId = null);
}
