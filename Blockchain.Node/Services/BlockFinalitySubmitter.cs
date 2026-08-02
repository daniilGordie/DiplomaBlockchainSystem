using Blockchain.Application.Blocks;
using Blockchain.Core.Consensus;

namespace Blockchain.Node.Services;

public interface IBlockFinalitySubmitter
{
    Task<BlockWriteResult> SubmitAsync(
        BlockProposal proposal,
        BlockModel sourceModel,
        CancellationToken cancellationToken = default);
}

public sealed class ImmediateBlockFinalitySubmitter : IBlockFinalitySubmitter
{
    private readonly CommittedBlockApplier _committedBlockApplier;

    public ImmediateBlockFinalitySubmitter(CommittedBlockApplier committedBlockApplier)
    {
        _committedBlockApplier = committedBlockApplier;
    }

    public Task<BlockWriteResult> SubmitAsync(
        BlockProposal proposal,
        BlockModel sourceModel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _committedBlockApplier.ApplyAsync(
            proposal.Block,
            sourceModel,
            broadcastToPeers: true);
    }
}

public sealed class EdgeBlockFinalitySubmitter : IBlockFinalitySubmitter
{
    private readonly IrohProposalForwarder _forwarder;
    private readonly EdgeCommittedBlockSyncService _edgeSync;

    public EdgeBlockFinalitySubmitter(
        IrohProposalForwarder forwarder,
        EdgeCommittedBlockSyncService edgeSync)
    {
        _forwarder = forwarder;
        _edgeSync = edgeSync;
    }

    public async Task<BlockWriteResult> SubmitAsync(
        BlockProposal proposal,
        BlockModel sourceModel,
        CancellationToken cancellationToken = default)
    {
        var result = await _forwarder.ForwardAsync(proposal, sourceModel, cancellationToken);
        if (result.Success)
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                var syncStatus = await _edgeSync.SyncOnceAsync(cancellationToken);
                if (syncStatus.LastAppliedBlocks > 0)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        return result;
    }
}
