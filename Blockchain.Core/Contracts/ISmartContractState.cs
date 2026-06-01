namespace Blockchain.Core.Contracts
{
    public interface ISmartContractStateReader
    {
        string? GetUserPublicKey(string userName);
        string GetUserRole(string projectId, string userName);
        int GetUserBalance(string userName);
        bool IsProjectExists(string projectId);
        string? GetProposalCreator(string proposalId);
    }

    public interface ISmartContractState : ISmartContractStateReader
    {
    }
}
