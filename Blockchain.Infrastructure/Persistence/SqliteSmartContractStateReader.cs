using Blockchain.Core.Contracts;

namespace Blockchain.Infrastructure.Persistence;

public sealed class SqliteSmartContractStateReader : ISmartContractState
{
    private readonly DatabaseManager _database;

    public SqliteSmartContractStateReader(DatabaseManager database)
    {
        _database = database;
    }

    public string? GetUserPublicKey(string userName) => _database.GetUserPublicKey(userName);
    public string GetUserRole(string projectId, string userName) => _database.GetUserRole(projectId, userName);
    public int GetUserBalance(string userName) => _database.GetUserBalance(userName);
    public bool IsProjectExists(string projectId) => _database.IsProjectExists(projectId);
    public string? GetProposalCreator(string proposalId) => _database.GetProposalCreator(proposalId);
}
