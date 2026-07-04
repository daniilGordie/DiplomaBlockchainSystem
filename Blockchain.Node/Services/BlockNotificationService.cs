using System.Text.Json;
using Blockchain.Node.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Blockchain.Node.Services;

public sealed class BlockNotificationService
{
    private readonly IHubContext<BlockchainHub> _hubContext;

    public BlockNotificationService(IHubContext<BlockchainHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyClientsAsync(BlockModel block)
    {
        string channelId = string.IsNullOrWhiteSpace(block.ChannelId) ? "System" : block.ChannelId;
        await _hubContext.Clients.Group(channelId).SendAsync("NewBlockBroadcast", block);

        if (string.IsNullOrWhiteSpace(block.Data) || !block.Data.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return;
        }

        using var doc = JsonDocument.Parse(block.Data);
        var root = doc.RootElement;
        string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;

        if (string.Equals(type, "AssignRole", StringComparison.Ordinal) &&
            root.TryGetProperty("TargetUser", out var assignedUserProp))
        {
            string assignedUser = assignedUserProp.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(assignedUser))
            {
                await _hubContext.Clients.Group($"USER_{assignedUser}").SendAsync("NewBlockBroadcast", block);
            }
        }

        if (string.Equals(type, "Transfer", StringComparison.Ordinal) &&
            root.TryGetProperty("TargetUser", out var targetUserProp) &&
            root.TryGetProperty("Amount", out var amountProp) &&
            root.TryGetProperty("User", out var senderProp))
        {
            string targetUser = targetUserProp.GetString() ?? string.Empty;
            int amount = amountProp.GetInt32();
            string sender = senderProp.GetString() ?? "Unknown";

            await _hubContext.Clients.Group($"USER_{targetUser}").SendAsync("FinancialTransferReceived", sender, amount);
        }
    }
}
