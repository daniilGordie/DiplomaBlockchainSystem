using Blockchain.Application.Git;
using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;

namespace Blockchain.Infrastructure.Services;

public sealed class DatabaseProjectMembershipReader : IProjectMembershipReader
{
    private readonly DatabaseManager _database;

    public DatabaseProjectMembershipReader(DatabaseManager database)
    {
        _database = database;
    }

    public string? GetUserPublicKey(string userName)
    {
        return _database.GetUserPublicKey(userName);
    }

    public string GetUserRole(string projectId, string userName)
    {
        return _database.GetUserRole(projectId, userName);
    }
}
