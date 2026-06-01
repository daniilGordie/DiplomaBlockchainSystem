using Blockchain.Node;
using Blockchain.UI.Models;

namespace Blockchain.UI.Application.Clients;

public interface IProjectWorkspaceDataService
{
    Task<ProjectWorkspaceData> LoadAsync(string currentProjectId);
    Task<IReadOnlyList<TaskHistoryItem>> LoadTaskHistoryAsync(string projectId, string taskId);
    Task<IReadOnlyList<GovernanceProposalItem>> LoadGovernanceAsync(string projectId);
    Task<ProjectDocumentsData> LoadDocumentsAsync(string projectId, string selectedDocumentId);
    Task<IReadOnlyList<DocumentVersionItem>> LoadDocumentVersionsAsync(string projectId, string documentId);
    Task<ProjectAnalyticsData> LoadAnalyticsAsync(string projectId);
}

public sealed record ProjectWorkspaceData(
    string CurrentProjectId,
    bool CurrentProjectChanged,
    IReadOnlyList<string> MyProjects,
    IReadOnlyList<string> OwnedProjects,
    IReadOnlyList<string> SharedProjects,
    IReadOnlyDictionary<string, string> ProjectRoles,
    string CurrentUserRole,
    IReadOnlyList<ProjectTask> Tasks,
    IReadOnlyList<BlockModel> Chain,
    IReadOnlyList<CommitPayloadUI> Commits,
    IReadOnlyList<ArtifactPayloadUI> Artifacts,
    IReadOnlyList<AuditTrailEntry> AuditTrail,
    IReadOnlyList<string> ProjectMembers);

public sealed record ProjectDocumentsData(
    IReadOnlyList<DocumentSummary> Documents,
    IReadOnlyList<DocumentVersionItem> Versions,
    string SelectedDocumentId,
    string DocumentTitle,
    string DocumentContent);

public sealed record ProjectAnalyticsData(
    AnalyticsResponse Analytics,
    SecurityAuditResponse SecurityAudit);
