using System.Text.Json;

namespace Blockchain.Application.Analytics;

public sealed class GetProjectAnalyticsUseCase
{
    private readonly IProjectAnalyticsReader _reader;
    private readonly IBlockAuditVerifier _verifier;

    public GetProjectAnalyticsUseCase(IProjectAnalyticsReader reader, IBlockAuditVerifier verifier)
    {
        _reader = reader;
        _verifier = verifier;
    }

    public ProjectAnalyticsResult Execute(string projectId)
    {
        var blocks = _reader.LoadChain(projectId);
        var state = _reader.LoadState(projectId);
        var payloadCounts = CountProjectPayloads(blocks);
        var securityAudit = GetSecurityAuditUseCase.Build(blocks.OrderBy(block => block.Index).ToList(), _verifier);

        double averageBlockIntervalSeconds = 0;
        double blocksPerMinute = 0;
        if (blocks.Count > 1)
        {
            var orderedBlocks = blocks.OrderBy(block => block.Index).ToList();
            var intervals = orderedBlocks
                .Skip(1)
                .Select((block, index) => Math.Abs((block.Timestamp - orderedBlocks[index].Timestamp).TotalSeconds))
                .Where(seconds => seconds > 0)
                .ToList();

            averageBlockIntervalSeconds = intervals.Count > 0 ? intervals.Average() : 0;
            var totalMinutes = Math.Max((orderedBlocks.Last().Timestamp - orderedBlocks.First().Timestamp).TotalMinutes, 1d / 60d);
            blocksPerMinute = Math.Round(orderedBlocks.Count / totalMinutes, 2);
        }

        return new ProjectAnalyticsResult(
            TotalBlocks: blocks.Count,
            TotalTasks: state.TotalTasks,
            TotalCommits: payloadCounts.CommitCount,
            TotalArtifacts: payloadCounts.ArtifactCount,
            TasksTodo: state.TasksTodo,
            TasksInProgress: state.TasksInProgress,
            TasksDone: state.TasksDone,
            UserReputation: state.UserReputation,
            TotalDocuments: state.TotalDocuments,
            AverageBlockIntervalSeconds: averageBlockIntervalSeconds,
            BlocksPerMinute: blocksPerMinute,
            SecurityFindings: securityAudit.FindingCount);
    }

    private static (int CommitCount, int ArtifactCount) CountProjectPayloads(IReadOnlyList<BlockSnapshot> blocks)
    {
        var commitHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var artifactHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Data))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(block.Data);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var root = doc.RootElement;
                string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                if (type == "CodeCommit" && root.TryGetProperty("CommitHash", out var commitHashProp))
                {
                    string commitHash = commitHashProp.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(commitHash))
                    {
                        commitHashes.Add(commitHash);
                    }
                }

                if (root.TryGetProperty("FileHash", out var fileHashProp))
                {
                    string fileHash = fileHashProp.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(fileHash))
                    {
                        artifactHashes.Add(fileHash);
                    }
                }
            }
            catch
            {
                // Malformed historical payloads are ignored in aggregate analytics.
            }
        }

        return (commitHashes.Count, artifactHashes.Count);
    }
}
