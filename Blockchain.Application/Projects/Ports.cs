namespace Blockchain.Application.Projects;

public interface IProjectTaskReader
{
    ProjectTaskListResult GetProjectTasks(string projectId, string userName);
    IReadOnlyList<TaskHistoryDto> GetTaskHistory(string projectId, string taskId);
}

public interface IGovernanceReader
{
    IReadOnlyList<GovernanceProposalDto> GetProposals(string projectId, string userName);
}

public interface IDocumentReader
{
    IReadOnlyList<DocumentSummaryDto> GetDocuments(string projectId);
    IReadOnlyList<DocumentVersionDto> GetDocumentVersions(string projectId, string documentId);
}
