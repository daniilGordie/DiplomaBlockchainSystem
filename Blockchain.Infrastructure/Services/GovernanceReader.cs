using Blockchain.Application.Projects;
using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Blockchain.Infrastructure.Services;

public sealed class GovernanceReader : IGovernanceReader
{
    private readonly DatabaseManager _database;
    private readonly ProjectReadConnectionFactory _connectionFactory;
    private readonly ILogger<GovernanceReader> _logger;

    public GovernanceReader(
        DatabaseManager database,
        ProjectReadConnectionFactory connectionFactory,
        ILogger<GovernanceReader> logger)
    {
        _database = database;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public IReadOnlyList<GovernanceProposalDto> GetProposals(string projectId, string userName)
    {
        var proposals = new List<GovernanceProposalDto>();

        try
        {
            _database.RefreshGovernanceStates(projectId);

            using var conn = new SqliteConnection(_connectionFactory.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT p.ProposalId, p.ProjectId, p.Title, p.Description, p.CreatedBy, p.CreatedAt,
                       p.Status, p.YesVotes, p.NoVotes, v.Vote
                FROM GovernanceProposals p
                LEFT JOIN GovernanceVotes v ON v.ProposalId = p.ProposalId AND v.UserName = $user
                WHERE p.ProjectId = $project
                ORDER BY p.CreatedAt DESC";
            cmd.Parameters.AddWithValue("$project", projectId);
            cmd.Parameters.AddWithValue("$user", userName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string userVote = "";
                if (!reader.IsDBNull(9))
                {
                    userVote = reader.GetInt32(9) == 1 ? "Yes" : "No";
                }

                proposals.Add(new GovernanceProposalDto(
                    reader.GetString(0),
                    reader.GetString(1),
                    _database.DecryptStoredValue(reader.GetString(2)),
                    reader.IsDBNull(3) ? "" : _database.DecryptStoredValue(reader.GetString(3)),
                    _database.DecryptStoredValue(reader.GetString(4)),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    userVote));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB Read Error in Governance");
        }

        return proposals;
    }
}
