using Blockchain.Application.Projects;
using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Blockchain.Infrastructure.Services;

public sealed class DocumentReader : IDocumentReader
{
    private readonly DatabaseManager _database;
    private readonly ProjectReadConnectionFactory _connectionFactory;
    private readonly ILogger<DocumentReader> _logger;

    public DocumentReader(
        DatabaseManager database,
        ProjectReadConnectionFactory connectionFactory,
        ILogger<DocumentReader> logger)
    {
        _database = database;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public IReadOnlyList<DocumentSummaryDto> GetDocuments(string projectId)
    {
        var documents = new List<DocumentSummaryDto>();

        try
        {
            using var conn = new SqliteConnection(_connectionFactory.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT d.DocumentId, d.Title, d.Version, d.UpdatedBy, d.UpdatedAt, d.ContentHash, d.Content
                FROM DocumentVersions d
                INNER JOIN (
                    SELECT DocumentId, MAX(Version) AS LatestVersion
                    FROM DocumentVersions
                    WHERE ProjectId = $project
                    GROUP BY DocumentId
                ) latest ON latest.DocumentId = d.DocumentId AND latest.LatestVersion = d.Version
                WHERE d.ProjectId = $project
                ORDER BY d.UpdatedAt DESC";
            cmd.Parameters.AddWithValue("$project", projectId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                documents.Add(new DocumentSummaryDto(
                    reader.GetString(0),
                    _database.DecryptStoredValue(reader.GetString(1)),
                    reader.GetInt32(2),
                    _database.DecryptStoredValue(reader.GetString(3)),
                    reader.GetString(4),
                    reader.GetString(5),
                    _database.DecryptStoredValue(reader.GetString(6))));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB Read Error in Documents");
        }

        return documents;
    }

    public IReadOnlyList<DocumentVersionDto> GetDocumentVersions(string projectId, string documentId)
    {
        var versions = new List<DocumentVersionDto>();

        try
        {
            using var conn = new SqliteConnection(_connectionFactory.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT DocumentId, Version, Title, Content, ContentHash, UpdatedBy, UpdatedAt
                FROM DocumentVersions
                WHERE ProjectId = $project AND DocumentId = $document
                ORDER BY Version DESC";
            cmd.Parameters.AddWithValue("$project", projectId);
            cmd.Parameters.AddWithValue("$document", documentId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                versions.Add(new DocumentVersionDto(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    _database.DecryptStoredValue(reader.GetString(2)),
                    _database.DecryptStoredValue(reader.GetString(3)),
                    reader.GetString(4),
                    _database.DecryptStoredValue(reader.GetString(5)),
                    reader.GetString(6)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB Read Error in DocumentVersions");
        }

        return versions;
    }
}
