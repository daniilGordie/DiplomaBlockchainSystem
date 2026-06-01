namespace Blockchain.Application.Projects;

public sealed class GetGovernanceProposalsUseCase
{
    private readonly IGovernanceReader _reader;

    public GetGovernanceProposalsUseCase(IGovernanceReader reader)
    {
        _reader = reader;
    }

    public IReadOnlyList<GovernanceProposalDto> Execute(string projectId, string userName)
    {
        return _reader.GetProposals(projectId, userName);
    }
}
