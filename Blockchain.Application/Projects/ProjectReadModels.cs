namespace Blockchain.Application.Projects;

public sealed record ProjectTaskDto(
    string Id,
    string Title,
    string Creator,
    string Assignee,
    int Status,
    string ProjectId,
    string Description,
    string ParentTaskId,
    string BranchInfo);

public sealed record ProjectTaskListResult(
    string UserRole,
    IReadOnlyList<ProjectTaskDto> Tasks);

public sealed record TaskHistoryDto(
    string Timestamp,
    string User,
    string Action,
    string StatusLabel,
    int BlockIndex);

public sealed record GovernanceProposalDto(
    string ProposalId,
    string ProjectId,
    string Title,
    string Description,
    string CreatedBy,
    string CreatedAt,
    string Status,
    int YesVotes,
    int NoVotes,
    string UserVote);

public sealed record DocumentSummaryDto(
    string DocumentId,
    string Title,
    int LatestVersion,
    string UpdatedBy,
    string UpdatedAt,
    string ContentHash,
    string Content);

public sealed record DocumentVersionDto(
    string DocumentId,
    int Version,
    string Title,
    string Content,
    string ContentHash,
    string UpdatedBy,
    string UpdatedAt);
