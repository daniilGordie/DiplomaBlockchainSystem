using Blockchain.Core;

namespace Blockchain.Infrastructure.Persistence;

public sealed class SqliteProjectMembershipStore : IProjectMembershipStore
{
    private readonly DatabaseManager _database;

    public SqliteProjectMembershipStore(DatabaseManager database)
    {
        _database = database;
    }

    public string GetUserRole(string projectId, string userName) => _database.GetUserRole(projectId, userName);
    public List<string> GetUserProjects(string userName) => _database.GetUserProjects(userName);
}
