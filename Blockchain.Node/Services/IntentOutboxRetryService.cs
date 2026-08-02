using System.Text.Json;
using Blockchain.Core.Consensus;
using Blockchain.Node.Endpoints;

namespace Blockchain.Node.Services;

public sealed class IntentOutboxRetryService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IIntentStore _intentStore;
    private readonly GrpcBlockProcessor _blockProcessor;
    private readonly ILogger<IntentOutboxRetryService> _logger;

    public IntentOutboxRetryService(
        IIntentStore intentStore,
        GrpcBlockProcessor blockProcessor,
        ILogger<IntentOutboxRetryService> logger)
    {
        _intentStore = intentStore;
        _blockProcessor = blockProcessor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RetryOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IntentOutbox] Retry loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    public async Task<int> RetryOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var records = _intentStore.LoadRetryableIntents(now, 25);
        int processed = 0;

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Status is IntentStatus.Committed or IntentStatus.Rejected or IntentStatus.Expired or IntentStatus.FailedPermanent)
            {
                continue;
            }

            if (record.AttemptCount > 0 && record.NextAttemptAtUtc is { } next && next > now)
            {
                continue;
            }

            processed++;
            var block = DeserializeBlock(record);
            if (block == null)
            {
                MarkPermanent(record, "stored block envelope cannot be read");
                continue;
            }

            int nextAttemptCount = record.AttemptCount + 1;
            _intentStore.UpdateIntentStatus(
                record.Intent.IntentId,
                IntentStatus.Submitted,
                nextAttemptCount,
                DateTime.UtcNow,
                DateTime.UtcNow,
                null,
                string.Empty,
                record.Destination,
                record.CommittedBlockHash,
                record.CommittedBlockIndex,
                record.ProposalId);

            var result = await _blockProcessor.ProcessReceivedAsync(block, cancellationToken);
            if (result.Success)
            {
                _intentStore.UpdateIntentStatus(
                    record.Intent.IntentId,
                    IntentStatus.Committed,
                    nextAttemptCount,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    null,
                    string.Empty,
                    record.Destination,
                    block.Hash,
                    block.Index,
                    record.ProposalId);
                continue;
            }

            bool retryable = IsRetryable(result.Message);
            _intentStore.UpdateIntentStatus(
                record.Intent.IntentId,
                retryable ? IntentStatus.FailedRetryable : IntentStatus.FailedPermanent,
                nextAttemptCount,
                DateTime.UtcNow,
                DateTime.UtcNow,
                retryable ? SignedIntentEndpoints.ComputeNextAttemptUtc(nextAttemptCount) : null,
                result.Message,
                record.Destination,
                null,
                null,
                record.ProposalId);
        }

        return processed;
    }

    private void MarkPermanent(IntentOutboxRecord record, string message)
    {
        _intentStore.UpdateIntentStatus(
            record.Intent.IntentId,
            IntentStatus.FailedPermanent,
            record.AttemptCount,
            DateTime.UtcNow,
            record.LastAttemptAtUtc,
            null,
            message,
            record.Destination,
            record.CommittedBlockHash,
            record.CommittedBlockIndex,
            record.ProposalId);
    }

    private static BlockModel? DeserializeBlock(IntentOutboxRecord record)
    {
        try
        {
            return JsonSerializer.Deserialize<BlockModel>(record.BlockEnvelopeJson, JsonOptions);
        }
        catch
        {
            return null;
        }
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
}
