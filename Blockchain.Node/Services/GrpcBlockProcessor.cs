using Blockchain.Application.Blocks;
using Blockchain.Core;
using Blockchain.Core.Consensus;

namespace Blockchain.Node.Services;

public sealed class GrpcBlockProcessor
{
    private readonly IBlockFinalitySubmitter _blockFinalitySubmitter;
    private readonly VerifyBlockProposalUseCase _verifyBlockProposal;
    private readonly BlockProposalFactory _blockProposalFactory;
    private readonly PeerBlockValidator _peerBlockValidator;
    private readonly ILogger<GrpcBlockProcessor> _logger;

    public GrpcBlockProcessor(
        IBlockFinalitySubmitter blockFinalitySubmitter,
        VerifyBlockProposalUseCase verifyBlockProposal,
        BlockProposalFactory blockProposalFactory,
        PeerBlockValidator peerBlockValidator,
        ILogger<GrpcBlockProcessor> logger)
    {
        _blockFinalitySubmitter = blockFinalitySubmitter;
        _verifyBlockProposal = verifyBlockProposal;
        _blockProposalFactory = blockProposalFactory;
        _peerBlockValidator = peerBlockValidator;
        _logger = logger;
    }

    public async Task<GrpcBlockProcessResult> ProcessReceivedAsync(
        BlockModel request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var block = ToBlock(request);
            var proposal = BuildProposal(request, block);
            if (!proposal.Accepted || proposal.Proposal == null)
            {
                return new GrpcBlockProcessResult(false, $"PoC proposal rejected: {proposal.Reason}", block.ChannelId);
            }

            var verification = _verifyBlockProposal.Execute(proposal.Proposal);
            if (!verification.Accepted)
            {
                return new GrpcBlockProcessResult(false, $"PoC verification rejected: {verification.Reason}", block.ChannelId);
            }

            var blockValidation = _peerBlockValidator.Validate(block);
            if (blockValidation.Status == PeerBlockValidationStatus.AlreadyAccepted)
            {
                return new GrpcBlockProcessResult(true, "Block already accepted", block.ChannelId);
            }

            if (blockValidation.Status != PeerBlockValidationStatus.Accepted)
            {
                return new GrpcBlockProcessResult(false, $"Block preflight rejected: {blockValidation.Reason}", block.ChannelId);
            }

            var committed = await _blockFinalitySubmitter.SubmitAsync(proposal.Proposal, request, cancellationToken);
            return new GrpcBlockProcessResult(committed.Success, committed.Message, committed.ChannelId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ReceiveBlock] Error");
            return new GrpcBlockProcessResult(false, $"Server Error: {ex.Message}", request.ChannelId);
        }
    }

    private BlockProposalBuildResult BuildProposal(BlockModel request, Block block)
    {
        var implicitProposal = _blockProposalFactory.BuildImplicitProposal(block);
        if (implicitProposal.Accepted)
        {
            return implicitProposal;
        }

        if (request.ContributionProof != null)
        {
            return BlockProposalBuildResult.Accept(new BlockProposal(
                block,
                GrpcProjectMapper.ToContributionProof(request.ContributionProof),
                DateTime.UtcNow));
        }

        return implicitProposal;
    }

    public static Block ToBlock(BlockModel model)
    {
        return new Block
        {
            Index = model.Index,
            Data = model.Data,
            PreviousHash = model.PreviousHash,
            Hash = model.Hash,
            Timestamp = model.TimestampUnixSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(model.TimestampUnixSeconds).UtcDateTime
                : DateTime.Parse(model.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind),
            TimestampUnixSeconds = model.TimestampUnixSeconds,
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
