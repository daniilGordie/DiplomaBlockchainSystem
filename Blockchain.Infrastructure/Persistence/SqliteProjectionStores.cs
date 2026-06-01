namespace Blockchain.Infrastructure.Persistence;

public sealed class SqliteTaskProjectionStore
{
    public SqliteTaskProjectionStore(DatabaseManager database)
    {
        Database = database;
    }

    public DatabaseManager Database { get; }
}

public sealed class SqliteDocumentStore
{
    public SqliteDocumentStore(DatabaseManager database)
    {
        Database = database;
    }

    public DatabaseManager Database { get; }
}

public sealed class SqliteGovernanceStore
{
    public SqliteGovernanceStore(DatabaseManager database)
    {
        Database = database;
    }

    public DatabaseManager Database { get; }
}
