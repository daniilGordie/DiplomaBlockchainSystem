using Blockchain.Application.Blocks;
using Blockchain.Core.Consensus;

namespace Blockchain.Node.Services;

public interface IBlockFinalitySubmitter
{
    Task<BlockWriteResult> SubmitAsync(BlockProposal proposal, BlockModel sourceModel);
}

public sealed class ImmediateBlockFinalitySubmitter : IBlockFinalitySubmitter
{
    private readonly CommittedBlockApplier _committedBlockApplier;

    public ImmediateBlockFinalitySubmitter(CommittedBlockApplier committedBlockApplier)
    {
        _committedBlockApplier = committedBlockApplier;
    }

    public Task<BlockWriteResult> SubmitAsync(BlockProposal proposal, BlockModel sourceModel)
    {
        return _committedBlockApplier.ApplyAsync(
            proposal.Block,
            sourceModel,
            broadcastToPeers: true);
    }
}

public sealed class EdgeBlockFinalitySubmitter : IBlockFinalitySubmitter
{
    private readonly IrohProposalForwarder _forwarder;

    public EdgeBlockFinalitySubmitter(IrohProposalForwarder forwarder)
    {
        _forwarder = forwarder;
    }

    public Task<BlockWriteResult> SubmitAsync(BlockProposal proposal, BlockModel sourceModel)
    {
        return _forwarder.ForwardAsync(proposal, sourceModel);
    }
}
