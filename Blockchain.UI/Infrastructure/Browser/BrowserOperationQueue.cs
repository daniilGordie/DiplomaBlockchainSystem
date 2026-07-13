using System.Text.Json;
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

            return JsonSerializer.Deserialize<List<BrowserQueuedOperation>>(json, JsonOptions) ?? new List<BrowserQueuedOperation>();
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
        string json = JsonSerializer.Serialize(items, JsonOptions);
        await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
