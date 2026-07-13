using System.Text.Json;
using System.Text;
using Microsoft.JSInterop;

namespace Blockchain.UI.Infrastructure.Browser;

public interface IBrowserOperationQueue
{
    Task<IReadOnlyList<BrowserQueuedOperation>> LoadAsync();
    Task EnqueueAsync(string operationType, string channelId, string payloadJson, string reason);
    Task RemoveAsync(string operationId);
    Task ClearCommittedAsync();
}

public sealed class BrowserOperationQueue : IBrowserOperationQueue
{
    private const string StorageKey = "nexus_browser_operation_queue_v1";
    private const int MaxQueuedOperations = 100;
    private readonly IJSRuntime _js;

    public BrowserOperationQueue(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<IReadOnlyList<BrowserQueuedOperation>> LoadAsync()
    {
        try
        {
            string? json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<BrowserQueuedOperation>();
            }

            return ParseQueue(json);
        }
        catch
        {
            return Array.Empty<BrowserQueuedOperation>();
        }
    }

    public async Task EnqueueAsync(string operationType, string channelId, string payloadJson, string reason)
    {
        var items = (await LoadAsync()).ToList();
        if (items.Count >= MaxQueuedOperations)
        {
            items.RemoveRange(0, items.Count - MaxQueuedOperations + 1);
        }

        items.Add(new BrowserQueuedOperation
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OperationType = operationType,
            ChannelId = channelId,
            PayloadJson = payloadJson,
            CreatedAtUtc = DateTime.UtcNow,
            LocalSubmitStatus = "QueuedLocally",
            RetryCount = 0,
            LastError = reason
        });
        await SaveAsync(items);
    }

    public async Task RemoveAsync(string operationId)
    {
        var items = (await LoadAsync())
            .Where(item => !string.Equals(item.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await SaveAsync(items);
    }

    public async Task ClearCommittedAsync()
    {
        var items = (await LoadAsync())
            .Where(item => !string.Equals(item.LocalSubmitStatus, "Submitted", StringComparison.OrdinalIgnoreCase))
            .ToList();
        await SaveAsync(items);
    }

    private async Task SaveAsync(IReadOnlyList<BrowserQueuedOperation> items)
    {
        string json = SerializeQueue(items);
        await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
    }

    private static IReadOnlyList<BrowserQueuedOperation> ParseQueue(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BrowserQueuedOperation>();
        }

        var items = new List<BrowserQueuedOperation>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            items.Add(new BrowserQueuedOperation
            {
                OperationId = GetString(item, nameof(BrowserQueuedOperation.OperationId)),
                OperationType = GetString(item, nameof(BrowserQueuedOperation.OperationType)),
                ChannelId = GetString(item, nameof(BrowserQueuedOperation.ChannelId)),
                PayloadJson = GetString(item, nameof(BrowserQueuedOperation.PayloadJson)),
                CreatedAtUtc = GetDateTime(item, nameof(BrowserQueuedOperation.CreatedAtUtc)),
                LocalSubmitStatus = GetString(item, nameof(BrowserQueuedOperation.LocalSubmitStatus)),
                RetryCount = GetInt32(item, nameof(BrowserQueuedOperation.RetryCount)),
                LastError = GetString(item, nameof(BrowserQueuedOperation.LastError))
            });
        }

        return items;
    }

    private static string SerializeQueue(IReadOnlyList<BrowserQueuedOperation> items)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString(nameof(BrowserQueuedOperation.OperationId), item.OperationId);
                writer.WriteString(nameof(BrowserQueuedOperation.OperationType), item.OperationType);
                writer.WriteString(nameof(BrowserQueuedOperation.ChannelId), item.ChannelId);
                writer.WriteString(nameof(BrowserQueuedOperation.PayloadJson), item.PayloadJson);
                writer.WriteString(nameof(BrowserQueuedOperation.CreatedAtUtc), item.CreatedAtUtc);
                writer.WriteString(nameof(BrowserQueuedOperation.LocalSubmitStatus), item.LocalSubmitStatus);
                writer.WriteNumber(nameof(BrowserQueuedOperation.RetryCount), item.RetryCount);
                writer.WriteString(nameof(BrowserQueuedOperation.LastError), item.LastError);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string GetString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out int value)
            ? value
            : 0;

    private static DateTime GetDateTime(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var property) && property.TryGetDateTime(out var value)
            ? value
            : DateTime.UtcNow;
}

public sealed class BrowserQueuedOperation
{
    public string OperationId { get; set; } = string.Empty;
    public string OperationType { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string LocalSubmitStatus { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public string LastError { get; set; } = string.Empty;
}
