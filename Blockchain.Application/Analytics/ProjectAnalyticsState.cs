namespace Blockchain.Application.Analytics;

public sealed record ProjectAnalyticsState(
    int TotalTasks,
    int TotalDocuments,
    int TasksTodo,
    int TasksInProgress,
    int TasksDone,
    IReadOnlyDictionary<string, int> UserReputation);

public sealed record ProjectAnalyticsResult(
    int TotalBlocks,
    int TotalTasks,
    int TotalCommits,
    int TotalArtifacts,
    int TasksTodo,
    int TasksInProgress,
    int TasksDone,
    IReadOnlyDictionary<string, int> UserReputation,
    int TotalDocuments,
    double AverageBlockIntervalSeconds,
    double BlocksPerMinute,
    int SecurityFindings);
