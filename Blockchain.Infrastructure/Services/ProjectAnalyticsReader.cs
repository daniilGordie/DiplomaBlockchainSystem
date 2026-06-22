using Blockchain.Application.Analytics;
using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Blockchain.Infrastructure.Services;

public sealed class ProjectAnalyticsReader : IProjectAnalyticsReader
{
    private readonly DatabaseManager _database;
    private readonly string _connectionString;

    public ProjectAnalyticsReader(DatabaseManager database, IConfiguration configuration)
    {
        _database = database;

        string dbPassword = configuration["NodeDbPassword"]
            ?? throw new InvalidOperationException(
                "NodeDbPassword is not configured. Set it via .NET user-secrets or environment variables.");

        _connectionString = $"Data Source={database.DbFileName};Password={dbPassword}";
    }

    public IReadOnlyList<BlockSnapshot> LoadChain(string projectId)
    {
        return _database.LoadChain(projectId)
            .Select(block => new BlockSnapshot(
                block.Index,
                block.Timestamp,
                block.Data,
                block.PreviousHash,
                block.Hash,
                block.ValidatorPublicKey ?? string.Empty,
                block.Signature ?? string.Empty,
                block.Nonce,
                block.ChannelId,
                block.TimestampUnixSeconds))
            .ToList();
    }

    public ProjectAnalyticsState LoadState(string projectId)
    {
        int totalTasks = 0;
        int totalDocuments = 0;
        int tasksTodo = 0;
        int tasksInProgress = 0;
        int tasksDone = 0;
        var userReputation = new Dictionary<string, int>(StringComparer.Ordinal);

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(1) FROM Tasks WHERE ProjectId = $p";
            cmd.Parameters.AddWithValue("$p", projectId);
            totalTasks = Convert.ToInt32(cmd.ExecuteScalar());
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(DISTINCT DocumentId) FROM DocumentVersions WHERE ProjectId = $p";
            cmd.Parameters.AddWithValue("$p", projectId);
            totalDocuments = Convert.ToInt32(cmd.ExecuteScalar());
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Status, COUNT(1) FROM Tasks WHERE ProjectId = $p GROUP BY Status";
            cmd.Parameters.AddWithValue("$p", projectId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int status = reader.GetInt32(0);
                int count = reader.GetInt32(1);
                if (status == 0) tasksTodo = count;
                else if (status == 1) tasksInProgress = count;
                else if (status == 2) tasksDone = count;
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT b.UserName, b.Amount
                FROM Balances b
                INNER JOIN ProjectMembers pm ON pm.UserName = b.UserName
                WHERE pm.ProjectId = $p";
            cmd.Parameters.AddWithValue("$p", projectId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                userReputation[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        return new ProjectAnalyticsState(
            totalTasks,
            totalDocuments,
            tasksTodo,
            tasksInProgress,
            tasksDone,
            userReputation);
    }
}
