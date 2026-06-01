using System.Text.Json;
using Blockchain.Application.Blocks;
using Blockchain.Core;
using Blockchain.Node.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Blockchain.Node.Services;

public sealed class GrpcBlockProcessor
{
    private readonly BroadcastLocalBlockUseCase _broadcastLocalBlock;
    private readonly ReceivePeerBlockUseCase _receivePeerBlock;
    private readonly P2PNetworkService _p2pService;
    private readonly IHubContext<BlockchainHub> _hubContext;
    private readonly ILogger<GrpcBlockProcessor> _logger;

    public GrpcBlockProcessor(
        BroadcastLocalBlockUseCase broadcastLocalBlock,
        ReceivePeerBlockUseCase receivePeerBlock,
        P2PNetworkService p2pService,
        IHubContext<BlockchainHub> hubContext,
        ILogger<GrpcBlockProcessor> logger)
    {
        _broadcastLocalBlock = broadcastLocalBlock;
        _receivePeerBlock = receivePeerBlock;
        _p2pService = p2pService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task<GrpcBlockProcessResult> ProcessBroadcastAsync(BlockModel request)
    {
        var peerBlock = ToBlock(request);
        var result = _broadcastLocalBlock.Execute(peerBlock);

        if (result.Success)
        {
            await NotifyClientsAsync(request);
        }

        return new GrpcBlockProcessResult(result.Success, result.Message, result.ChannelId);
    }

    public async Task<GrpcBlockProcessResult> ProcessReceivedAsync(BlockModel request)
    {
        try
        {
            var block = ToBlock(request);
            var result = _receivePeerBlock.Execute(new ReceivePeerBlockCommand(block, request.Timestamp));
            if (!result.Success)
            {
                return new GrpcBlockProcessResult(false, result.Message, result.ChannelId);
            }

            await NotifyClientsAsync(request);
            await _p2pService.BroadcastBlockAsync(block);

            return new GrpcBlockProcessResult(true, result.Message, result.ChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReceiveBlock] Error");
            return new GrpcBlockProcessResult(false, $"Server Error: {ex.Message}", request.ChannelId);
        }
    }

    private async Task NotifyClientsAsync(BlockModel block)
    {
        string channelId = string.IsNullOrWhiteSpace(block.ChannelId) ? "System" : block.ChannelId;
        await _hubContext.Clients.Group(channelId).SendAsync("NewBlockBroadcast", block);

        if (string.IsNullOrWhiteSpace(block.Data) || !block.Data.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return;
        }

        using var doc = JsonDocument.Parse(block.Data);
        var root = doc.RootElement;
        string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";

        if (string.Equals(type, "AssignRole", StringComparison.Ordinal) &&
            root.TryGetProperty("TargetUser", out var assignedUserProp))
        {
            string assignedUser = assignedUserProp.GetString() ?? "";
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
            string targetUser = targetUserProp.GetString() ?? "";
            int amount = amountProp.GetInt32();
            string sender = senderProp.GetString() ?? "Unknown";

            await _hubContext.Clients.Group($"USER_{targetUser}").SendAsync("FinancialTransferReceived", sender, amount);
        }
    }

    public static Block ToBlock(BlockModel model)
    {
        return new Block
        {
            Index = model.Index,
            Data = model.Data,
            PreviousHash = model.PreviousHash,
            Hash = model.Hash,
            Timestamp = DateTime.Parse(model.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind),
            ValidatorPublicKey = model.ValidatorPublicKey,
            Signature = model.Signature,
            Nonce = model.Nonce,
            ChannelId = string.IsNullOrWhiteSpace(model.ChannelId) ? "System" : model.ChannelId
        };
    }
}

public sealed record GrpcBlockProcessResult(
    bool Success,
    string Message,
    string ChannelId);
