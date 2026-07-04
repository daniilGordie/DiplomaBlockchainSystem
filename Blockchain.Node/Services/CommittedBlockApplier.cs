using Blockchain.Application.Blocks;
using Blockchain.Core;

namespace Blockchain.Node.Services;

public sealed class CommittedBlockApplier
{
    private readonly ReceivePeerBlockUseCase _receivePeerBlock;
    private readonly BlockNotificationService _notifications;
    private readonly P2PNetworkService _p2pService;
    private readonly IBlockFinalityMetadataStore _finalityMetadata;

    public CommittedBlockApplier(
        ReceivePeerBlockUseCase receivePeerBlock,
        BlockNotificationService notifications,
        P2PNetworkService p2pService,
        IBlockFinalityMetadataStore finalityMetadata)
    {
        _receivePeerBlock = receivePeerBlock;
        _notifications = notifications;
        _p2pService = p2pService;
        _finalityMetadata = finalityMetadata;
    }

    public async Task<BlockWriteResult> ApplyAsync(
        Block block,
        BlockModel sourceModel,
        bool broadcastToPeers,
        BlockFinalityMetadata? finalityMetadata = null)
    {
        var result = _receivePeerBlock.Execute(new ReceivePeerBlockCommand(block));
        if (!result.Success)
        {
            return result;
        }

        _finalityMetadata.SaveFinalityMetadata(finalityMetadata ?? new BlockFinalityMetadata(
            block.Hash,
            block.ChannelId,
            broadcastToPeers ? ConsensusFinalityModes.Immediate : "Committed",
            null,
            null,
            DateTime.UtcNow));

        await _notifications.NotifyClientsAsync(sourceModel);

        if (broadcastToPeers)
        {
            await _p2pService.BroadcastBlockAsync(block);
        }

        return result;
    }
}
