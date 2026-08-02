using Blockchain.Application.Blocks;
using Blockchain.Core;

namespace Blockchain.Node.Services;

public sealed class CommittedBlockApplier
{
    private readonly ReceivePeerBlockUseCase _receivePeerBlock;
    private readonly BlockNotificationService _notifications;
    private readonly IBlockFinalityMetadataStore _finalityMetadata;

    public CommittedBlockApplier(
        ReceivePeerBlockUseCase receivePeerBlock,
        BlockNotificationService notifications,
        IBlockFinalityMetadataStore finalityMetadata)
    {
        _receivePeerBlock = receivePeerBlock;
        _notifications = notifications;
        _finalityMetadata = finalityMetadata;
    }

    public async Task<BlockWriteResult> ApplyAsync(
        Block block,
        BlockModel sourceModel,
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
            ConsensusFinalityModes.Immediate,
            null,
            null,
            DateTime.UtcNow));

        if (sourceModel.ContributionProof != null)
        {
            _finalityMetadata.SaveContributionProof(
                block.Hash,
                GrpcProjectMapper.ToContributionProof(sourceModel.ContributionProof));
        }

        await _notifications.NotifyClientsAsync(sourceModel);

        return result;
    }
}
