using Blockchain.Core.Consensus;

namespace Blockchain.Application.Blocks;

public sealed class VerifyBlockProposalUseCase
{
    private readonly PoCVerifier _pocVerifier;

    public VerifyBlockProposalUseCase(PoCVerifier pocVerifier)
    {
        _pocVerifier = pocVerifier;
    }

    public BlockProposalVerificationResult Execute(BlockProposal proposal)
    {
        var result = _pocVerifier.Verify(proposal);
        return new BlockProposalVerificationResult(result.Accepted, result.Reason);
    }
}

public sealed record BlockProposalVerificationResult(
    bool Accepted,
    string Reason);
