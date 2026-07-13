using Blockchain.Core;
using Blockchain.Core.Consensus;
using Blockchain.Application.Git;
using Blockchain.Node.Services;
using System.Text.Json;

namespace Blockchain.Node.Endpoints;

public static class SignedIntentEndpoints
{
    public static IEndpointRouteBuilder MapSignedIntentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/network/intents/submit", async (
            SignedIntentSubmitRequest request,
            IConfiguration configuration,
            SignedIntentVerifier intentVerifier,
            IRequestReplayGuard replayGuard,
            IIntentStore intentStore,
            GrpcBlockProcessor blockProcessor,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logger = loggerFactory.CreateLogger("SignedIntentEndpoints");

            string networkId = FirstNonEmpty(configuration["Network:Id"], configuration["NetworkId"], "nexus-main");
            var intent = request.Intent with
            {
                NetworkId = string.IsNullOrWhiteSpace(request.Intent.NetworkId) ? networkId : request.Intent.NetworkId,
                ChannelId = ChannelName.Normalize(request.Intent.ChannelId),
                ProjectId = string.IsNullOrWhiteSpace(request.Intent.ProjectId)
                    ? ChannelName.Normalize(request.Intent.ChannelId)
                    : request.Intent.ProjectId
            };

            var existing = intentStore.GetIntent(intent.IntentId);
            if (existing != null)
            {
                return Results.Json(ToSubmitResponse(existing, "intent already known"));
            }

            if (!string.Equals(intent.NetworkId, networkId, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Rejected signed intent {IntentId}: network id mismatch. Expected {ExpectedNetworkId}, got {ActualNetworkId}.",
                    intent.IntentId,
                    networkId,
                    intent.NetworkId);
                return Results.BadRequest(new SignedIntentSubmitResponse(false, "intent network id does not match this node", string.Empty, string.Empty));
            }

            var validation = intentVerifier.Validate(intent);
            if (!validation.Accepted)
            {
                logger.LogWarning("Rejected signed intent {IntentId}: {Reason}.", intent.IntentId, validation.Reason);
                return Results.BadRequest(new SignedIntentSubmitResponse(false, $"intent rejected: {validation.Reason}", string.Empty, string.Empty));
            }

            if (!replayGuard.TryRegister($"intent:{validation.IntentId}", DateTime.UtcNow))
            {
                return Results.Conflict(new SignedIntentSubmitResponse(false, "duplicate intent", validation.IntentId, intent.ChannelId));
            }

            intent = intent with { IntentId = validation.IntentId };
            var envelopeValidation = ValidateBlockEnvelope(intent, request.Block);
            if (!envelopeValidation.Accepted)
            {
                logger.LogWarning("Rejected signed intent {IntentId}: block envelope rejected: {Reason}.", validation.IntentId, envelopeValidation.Reason);
                return Results.BadRequest(new SignedIntentSubmitResponse(false, $"block envelope rejected: {envelopeValidation.Reason}", validation.IntentId, intent.ChannelId));
            }

            var now = DateTime.UtcNow;
            string proposalId = $"proposal:{validation.IntentId}";
            intentStore.SaveIntent(new IntentOutboxRecord(
                intent,
                JsonSerializer.Serialize(request.Block, JsonOptions),
                IntentStatus.Submitted,
                0,
                now,
                now,
                null,
                now.AddSeconds(30),
                string.Empty,
                "local-node",
                null,
                null,
                proposalId));

            intentStore.UpdateIntentStatus(
                validation.IntentId,
                IntentStatus.Accepted,
                0,
                DateTime.UtcNow,
                null,
                DateTime.UtcNow,
                string.Empty,
                "local-node",
                null,
                null,
                proposalId);

            intentStore.UpdateIntentStatus(
                validation.IntentId,
                IntentStatus.WaitingForProducer,
                0,
                DateTime.UtcNow,
                null,
                DateTime.UtcNow,
                string.Empty,
                "local-node",
                null,
                null,
                proposalId);

            intentStore.UpdateIntentStatus(
                validation.IntentId,
                IntentStatus.WaitingForConsensus,
                1,
                DateTime.UtcNow,
                DateTime.UtcNow,
                null,
                string.Empty,
                "local-node",
                null,
                null,
                proposalId);

            var result = await blockProcessor.ProcessReceivedAsync(request.Block);
            var status = result.Success
                ? IntentStatus.Committed
                : IsRetryable(result.Message) ? IntentStatus.FailedRetryable : IntentStatus.FailedPermanent;
            intentStore.UpdateIntentStatus(
                validation.IntentId,
                status,
                1,
                DateTime.UtcNow,
                DateTime.UtcNow,
                result.Success ? null : ComputeNextAttemptUtc(1),
                result.Success ? string.Empty : result.Message,
                "local-node",
                result.Success ? request.Block.Hash : null,
                result.Success ? request.Block.Index : null,
                proposalId);

            return Results.Json(new SignedIntentSubmitResponse(
                result.Success,
                result.Message,
                validation.IntentId,
                result.ChannelId,
                status.ToString(),
                result.Success ? request.Block.Hash : string.Empty,
                result.Success ? request.Block.Index : null,
                proposalId));
        });

        endpoints.MapGet("/api/network/intents", (
            IIntentStore intentStore,
            int? limit) =>
        {
            var intents = intentStore.LoadRecentIntents(Math.Clamp(limit ?? 50, 1, 200))
                .Select(ToStatusResponse)
                .ToArray();
            return Results.Json(new IntentListResponse(intents));
        });

        endpoints.MapGet("/api/network/intents/{intentId}", (
            string intentId,
            IIntentStore intentStore) =>
        {
            var intent = intentStore.GetIntent(intentId);
            return intent == null
                ? Results.NotFound(new IntentStatusResponse(intentId, string.Empty, string.Empty, "unknown", 0, null, null, string.Empty, string.Empty, string.Empty, null, string.Empty, Array.Empty<IntentTransitionResponse>()))
                : Results.Json(ToStatusResponse(intent, intentStore.LoadIntentHistory(intentId)));
        });

        return endpoints;
    }

    private static SignedIntentEnvelopeValidationResult ValidateBlockEnvelope(SignedIntent intent, BlockModel block)
    {
        if (block == null)
        {
            return SignedIntentEnvelopeValidationResult.Reject("block envelope is required until producer materialization is enabled");
        }

        string channelId = ChannelName.Normalize(block.ChannelId);
        if (!string.Equals(channelId, intent.ChannelId, StringComparison.OrdinalIgnoreCase))
        {
            return SignedIntentEnvelopeValidationResult.Reject("block channel does not match intent channel");
        }

        if (!string.Equals(block.Data, intent.PayloadJson, StringComparison.Ordinal))
        {
            return SignedIntentEnvelopeValidationResult.Reject("block payload does not match intent payload");
        }

        if (!string.Equals(block.ValidatorPublicKey, intent.ActorPublicKey, StringComparison.Ordinal))
        {
            return SignedIntentEnvelopeValidationResult.Reject("block signer does not match intent actor");
        }

        string payloadType = TryGetPayloadType(intent.PayloadJson);
        if (!string.IsNullOrWhiteSpace(payloadType) &&
            !string.Equals(payloadType, intent.OperationType, StringComparison.OrdinalIgnoreCase))
        {
            return SignedIntentEnvelopeValidationResult.Reject("intent operation type does not match payload type");
        }

        return SignedIntentEnvelopeValidationResult.Accept();
    }

    private static string TryGetPayloadType(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("Type", out var type) ||
                doc.RootElement.TryGetProperty("type", out type))
            {
                return type.GetString() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static bool IsRetryable(string message)
    {
        return message.Contains("leader", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("majority", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("P2P block finality is disabled", StringComparison.OrdinalIgnoreCase);
    }

    internal static DateTime ComputeNextAttemptUtc(int attemptCount)
    {
        int baseDelaySeconds = Math.Min(300, (int)Math.Pow(2, Math.Clamp(attemptCount, 1, 8)));
        int jitterSeconds = Random.Shared.Next(0, Math.Min(30, baseDelaySeconds + 1));
        return DateTime.UtcNow.AddSeconds(baseDelaySeconds + jitterSeconds);
    }

    private static SignedIntentSubmitResponse ToSubmitResponse(IntentOutboxRecord record, string message) =>
        new(
            record.Status == IntentStatus.Committed,
            string.IsNullOrWhiteSpace(record.LastError) ? message : record.LastError,
            record.Intent.IntentId,
            record.Intent.ChannelId,
            record.Status.ToString(),
            record.CommittedBlockHash ?? string.Empty,
            record.CommittedBlockIndex,
            record.ProposalId ?? string.Empty);

    private static IntentStatusResponse ToStatusResponse(IntentOutboxRecord record) =>
        ToStatusResponse(record, Array.Empty<IntentStatusTransition>());

    private static IntentStatusResponse ToStatusResponse(IntentOutboxRecord record, IReadOnlyList<IntentStatusTransition> history) =>
        new(
            record.Intent.IntentId,
            record.Intent.CorrelationId,
            record.Intent.OperationType,
            record.Status.ToString(),
            record.AttemptCount,
            record.LastAttemptAtUtc,
            record.NextAttemptAtUtc,
            record.LastError,
            record.Destination,
            record.CommittedBlockHash ?? string.Empty,
            record.CommittedBlockIndex,
            record.ProposalId ?? string.Empty,
            history.Select(item => new IntentTransitionResponse(
                item.Status.ToString(),
                item.ChangedAtUtc,
                item.Message,
                item.Destination,
                item.ProposalId ?? string.Empty,
                item.CommittedBlockHash ?? string.Empty,
                item.CommittedBlockIndex)).ToArray());

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record SignedIntentSubmitRequest(
    SignedIntent Intent,
    BlockModel Block);

public sealed record SignedIntentSubmitResponse(
    bool Success,
    string Message,
    string IntentId,
    string ChannelId,
    string Status = "",
    string CommittedBlockHash = "",
    long? CommittedBlockIndex = null,
    string ProposalId = "");

public sealed record IntentStatusResponse(
    string IntentId,
    string CorrelationId,
    string OperationType,
    string Status,
    int AttemptCount,
    DateTime? LastAttemptAtUtc,
    DateTime? NextAttemptAtUtc,
    string LastError,
    string Destination,
    string CommittedBlockHash,
    long? CommittedBlockIndex,
    string ProposalId,
    IReadOnlyList<IntentTransitionResponse> History);

public sealed record IntentTransitionResponse(
    string Status,
    DateTime ChangedAtUtc,
    string Message,
    string Destination,
    string ProposalId,
    string CommittedBlockHash,
    long? CommittedBlockIndex);

public sealed record IntentListResponse(IReadOnlyList<IntentStatusResponse> Intents);

public sealed record SignedIntentEnvelopeValidationResult(
    bool Accepted,
    string Reason)
{
    public static SignedIntentEnvelopeValidationResult Accept() => new(true, "accepted");
    public static SignedIntentEnvelopeValidationResult Reject(string reason) => new(false, reason);
}
