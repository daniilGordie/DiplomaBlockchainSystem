using Blockchain.Application.Analytics;

namespace Blockchain.Node.Services;

public static class GrpcAnalyticsMapper
{
    public static AnalyticsResponse ToAnalyticsResponse(ProjectAnalyticsResult analytics)
    {
        var response = new AnalyticsResponse
        {
            TotalBlocks = analytics.TotalBlocks,
            TotalTasks = analytics.TotalTasks,
            TotalCommits = analytics.TotalCommits,
            TotalArtifacts = analytics.TotalArtifacts,
            TasksTodo = analytics.TasksTodo,
            TasksInProgress = analytics.TasksInProgress,
            TasksDone = analytics.TasksDone,
            TotalDocuments = analytics.TotalDocuments,
            AverageBlockIntervalSeconds = analytics.AverageBlockIntervalSeconds,
            BlocksPerMinute = analytics.BlocksPerMinute,
            SecurityFindings = analytics.SecurityFindings
        };

        foreach (var reputation in analytics.UserReputation)
        {
            response.UserReputation.Add(reputation.Key, reputation.Value);
        }

        return response;
    }

    public static SecurityAuditResponse ToSecurityAuditResponse(SecurityAuditResult audit)
    {
        var response = new SecurityAuditResponse
        {
            ChainValid = audit.ChainValid,
            CheckedBlocks = audit.CheckedBlocks,
            InvalidHashes = audit.InvalidHashes,
            InvalidSignatures = audit.InvalidSignatures,
            InvalidProofOfWork = audit.InvalidProofOfWork,
            BrokenLinks = audit.BrokenLinks,
            InvalidFinalityMetadata = audit.InvalidFinalityMetadata
        };

        foreach (var item in audit.Items)
        {
            response.Items.Add(new SecurityAuditItem
            {
                Severity = item.Severity,
                CheckName = item.CheckName,
                Details = item.Details
            });
        }

        return response;
    }
}
