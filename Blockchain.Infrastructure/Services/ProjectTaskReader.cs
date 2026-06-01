using System.Text.Json;
using Blockchain.Application.Projects;
using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Blockchain.Infrastructure.Services;

public sealed class ProjectTaskReader : IProjectTaskReader
{
    private readonly DatabaseManager _database;
    private readonly ProjectReadConnectionFactory _connectionFactory;
    private readonly ILogger<ProjectTaskReader> _logger;

    public ProjectTaskReader(
        DatabaseManager database,
        ProjectReadConnectionFactory connectionFactory,
        ILogger<ProjectTaskReader> logger)
    {
        _database = database;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public ProjectTaskListResult GetProjectTasks(string projectId, string userName)
    {
        string role = _database.GetUserRole(projectId, userName);
        var tasks = new List<ProjectTaskDto>();

        if (role == "None" && projectId != "System")
        {
            return new ProjectTaskListResult(role, tasks);
        }

        try
        {
            using var conn = new SqliteConnection(_connectionFactory.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = "SELECT TaskId, Title, Creator, Assignee, Status, ProjectId, Description, ParentTaskId, BranchInfo FROM Tasks WHERE ProjectId = $p";
            cmd.Parameters.AddWithValue("$p", projectId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add(new ProjectTaskDto(
                    reader.GetString(0),
                    _database.DecryptStoredValue(reader.GetString(1)),
                    _database.DecryptStoredValue(reader.GetString(2)),
                    _database.DecryptStoredValue(reader.GetString(3)),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? "" : _database.DecryptStoredValue(reader.GetString(6)),
                    reader.IsDBNull(7) ? "" : _database.DecryptStoredValue(reader.GetString(7)),
                    reader.IsDBNull(8) ? "" : _database.DecryptStoredValue(reader.GetString(8))));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB Read Error in Tasks");
        }

        return new ProjectTaskListResult(role, tasks);
    }

    public IReadOnlyList<TaskHistoryDto> GetTaskHistory(string projectId, string taskId)
    {
        var result = new List<TaskHistoryDto>();
        var blocks = _database.LoadChain(projectId);

        foreach (var block in blocks)
        {
            if (!block.Data.Contains(taskId, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(block.Data);
                var root = doc.RootElement;
                if (root.TryGetProperty("TaskId", out var taskIdProp) && taskIdProp.GetString() == taskId)
                {
                    string action = root.TryGetProperty("Type", out var type) ? type.GetString() ?? "Update" : "Update";
                    string user = root.TryGetProperty("User", out var userProp) ? userProp.GetString() ?? "System" : "System";
                    int statusId = root.TryGetProperty("Status", out var status) ? status.GetInt32() : 0;
                    string statusLabel = statusId == 0 ? "To Do" : statusId == 1 ? "In Progress" : "Done";

                    result.Add(new TaskHistoryDto(
                        block.Timestamp.ToString("g"),
                        user,
                        action,
                        statusLabel,
                        block.Index));
                }
            }
            catch
            {
                // Ignore malformed historical task payloads.
            }
        }

        return result;
    }
}
