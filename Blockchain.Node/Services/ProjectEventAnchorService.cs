using System.Text.Json;
using Blockchain.Core;
using Blockchain.Node.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Blockchain.Node.Services;

public sealed class ProjectEventAnchorService
{
    private readonly BlockchainManager _blockchainManager;
    private readonly OracleIdentity _oracleIdentity;
    private readonly P2PNetworkService _p2pService;
    private readonly IHubContext<BlockchainHub> _hubContext;

    public ProjectEventAnchorService(
        BlockchainManager blockchainManager,
        OracleIdentity oracleIdentity,
        P2PNetworkService p2pService,
        IHubContext<BlockchainHub> hubContext)
    {
        _blockchainManager = blockchainManager;
        _oracleIdentity = oracleIdentity;
        _p2pService = p2pService;
        _hubContext = hubContext;
    }

    public async Task<ProjectEventAnchorResult> AnchorAsync<TPayload>(
        string channelId,
        TPayload payload,
        string timestamp)
    {
        var latest = _blockchainManager.GetLatestBlock(channelId);
        int nextIndex = latest != null ? latest.Index + 1 : 0;
        string previousHash = latest != null ? latest.Hash : "0";
        string blockData = JsonSerializer.Serialize(payload);
        var blockTimestamp = DateTimeOffset.Parse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind);
        long timestampUnixSeconds = blockTimestamp.ToUnixTimeSeconds();
        string signableData = $"{nextIndex}{timestampUnixSeconds}{blockData}{previousHash}";
        string oracleSignature = _oracleIdentity.SignData(signableData);

        var block = new Block
        {
            Index = nextIndex,
            Timestamp = blockTimestamp.UtcDateTime,
            TimestampUnixSeconds = timestampUnixSeconds,
            Data = blockData,
            PreviousHash = previousHash,
            ValidatorPublicKey = _oracleIdentity.PublicKey,
            Signature = oracleSignature,
            ChannelId = channelId
        };

        _blockchainManager.MineBlock(block);
        bool accepted = _blockchainManager.ProcessPeerBlock(block);
        if (!accepted)
        {
            return new ProjectEventAnchorResult(false, channelId, "", "blockchain_validation_failed");
        }

        await _hubContext.Clients.Group(channelId).SendAsync("NewBlockBroadcast", ToBlockModel(block));
        await _p2pService.BroadcastBlockAsync(block);

        return new ProjectEventAnchorResult(true, channelId, block.Hash, "");
    }

    private static BlockModel ToBlockModel(Block block)
    {
        return new BlockModel
        {
            Index = block.Index,
            Timestamp = block.Timestamp.ToString("O"),
            TimestampUnixSeconds = block.TimestampUnixSeconds,
            Data = block.Data,
            PreviousHash = block.PreviousHash,
            Hash = block.Hash,
            ValidatorPublicKey = block.ValidatorPublicKey ?? string.Empty,
            Signature = block.Signature ?? string.Empty,
            Nonce = block.Nonce,
            ChannelId = block.ChannelId
        };
    }
}

public sealed record ProjectEventAnchorResult(
    bool Accepted,
    string ChannelId,
    string BlockHash,
    string Error);
