using System.Text.Json;
using Blockchain.Node;
using Blockchain.UI.Application.Security;
using Blockchain.UI.Models;
using Blockchain.UI.Services;

namespace Blockchain.UI.Application.Clients;

public sealed class ProjectWorkspaceDataService : IProjectWorkspaceDataService
{
    private readonly BlockchainService.BlockchainServiceClient _blockchainClient;
    private readonly IReadRequestAuthorizer _readAuthorizer;
    private readonly KeyService _keyService;

    public ProjectWorkspaceDataService(
        BlockchainService.BlockchainServiceClient blockchainClient,
        IReadRequestAuthorizer readAuthorizer,
        KeyService keyService)
    {
        _blockchainClient = blockchainClient;
        _readAuthorizer = readAuthorizer;
        _keyService = keyService;
    }

    public async Task<ProjectWorkspaceData> LoadAsync(string currentProjectId)
    {
        string nextProjectId = string.IsNullOrWhiteSpace(currentProjectId) ? "System" : currentProjectId;
        var myProjects = new List<string>();
        var projectRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool currentProjectChanged = false;

        if (_keyService.IsLoggedIn)
        {
            var projectsRequest = new UserRequest
            {
                UserName = _keyService.UserName,
                UserPublicKey = _keyService.PublicKey ?? ""
            };
            _readAuthorizer.Apply(projectsRequest, $"USER:{_keyService.UserName}:PROJECTS");
            var projectsResponse = await _blockchainClient.GetMyProjectsAsync(projectsRequest);
            myProjects = projectsResponse.ProjectIds.ToList();
            projectRoles = await LoadProjectRolesAsync(myProjects);

            if ((!myProjects.Contains(nextProjectId, StringComparer.OrdinalIgnoreCase) || nextProjectId == "System") && myProjects.Count > 0)
            {
                nextProjectId = myProjects.First();
                currentProjectChanged = true;
            }
        }

        var tasksResponse = await LoadTasksResponseAsync(nextProjectId);
        string currentUserRole = string.IsNullOrWhiteSpace(tasksResponse.UserRole) ? "None" : tasksResponse.UserRole;
        var tasks = tasksResponse.Tasks.Select(ToProjectTask).ToList();

        if (projectRoles.TryGetValue(nextProjectId, out var roleFromBuckets))
        {
            currentUserRole = roleFromBuckets;
        }
        else if (!myProjects.Contains(nextProjectId, StringComparer.OrdinalIgnoreCase))
        {
            currentUserRole = "None";
        }

        var chain = new List<BlockModel>();
        var commits = new List<CommitPayloadUI>();
        var artifacts = new List<ArtifactPayloadUI>();
        var auditTrail = new List<AuditTrailEntry>();
        var projectMembers = new List<string>();

        if (tasks.Count > 0 || _keyService.IsLoggedIn)
        {
            var chainResponse = await LoadChainAsync(nextProjectId, 50);
            chain = chainResponse.Blocks.ToList();
            commits = BuildCommits(chain);
            artifacts = BuildArtifacts(chain);
            auditTrail = BuildAuditTrail(chain);
            projectMembers = await LoadProjectMembersAsync(nextProjectId, currentUserRole, tasks);
        }

        var ownedProjects = myProjects
            .Where(projectId => projectRoles.TryGetValue(projectId, out var role) &&
                                string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            .OrderBy(projectId => projectId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sharedProjects = myProjects
            .Where(projectId => !projectRoles.TryGetValue(projectId, out var role) ||
                                !string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            .OrderBy(projectId => projectId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProjectWorkspaceData(
            nextProjectId,
            currentProjectChanged,
            myProjects,
            ownedProjects,
            sharedProjects,
            projectRoles,
            currentUserRole,
            tasks,
            chain,
            commits,
            artifacts,
            auditTrail,
            projectMembers);
    }

    public async Task<IReadOnlyList<TaskHistoryItem>> LoadTaskHistoryAsync(string projectId, string taskId)
    {
        var request = new TaskHistoryRequest
        {
            TaskId = taskId,
            ProjectId = projectId,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? ""
        };
        _readAuthorizer.Apply(request, $"PROJECT:{projectId}:TASK_HISTORY:{taskId}");
        var response = await _blockchainClient.GetTaskHistoryAsync(request);
        return response.Items.OrderByDescending(x => x.Timestamp).ToList();
    }

    public async Task<IReadOnlyList<GovernanceProposalItem>> LoadGovernanceAsync(string projectId)
    {
        var request = new ProjectRequest
        {
            ProjectId = projectId,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? ""
        };
        _readAuthorizer.Apply(request, $"PROJECT:{projectId}:GOVERNANCE");
        var response = await _blockchainClient.GetGovernanceProposalsAsync(request);
        return response.Proposals.ToList();
    }

    public async Task<ProjectDocumentsData> LoadDocumentsAsync(string projectId, string selectedDocumentId)
    {
        var request = new ProjectRequest
        {
            ProjectId = projectId,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? ""
        };
        _readAuthorizer.Apply(request, $"PROJECT:{projectId}:DOCUMENTS");
        var response = await _blockchainClient.GetProjectDocumentsAsync(request);
        var documents = response.Documents.ToList();

        if (string.IsNullOrWhiteSpace(selectedDocumentId))
        {
            return new ProjectDocumentsData(documents, new List<DocumentVersionItem>(), "", "", "");
        }

        var selected = documents.FirstOrDefault(document => document.DocumentId == selectedDocumentId);
        if (selected == null)
        {
            return new ProjectDocumentsData(documents, new List<DocumentVersionItem>(), "", "", "");
        }

        var versions = await LoadDocumentVersionsAsync(projectId, selectedDocumentId);
        return new ProjectDocumentsData(
            documents,
            versions,
            selectedDocumentId,
            selected.Title,
            selected.Content);
    }

    public async Task<IReadOnlyList<DocumentVersionItem>> LoadDocumentVersionsAsync(string projectId, string documentId)
    {
        var request = new DocumentHistoryRequest
        {
            ProjectId = projectId,
            DocumentId = documentId,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? ""
        };
        _readAuthorizer.Apply(request, $"PROJECT:{projectId}:DOCUMENT:{documentId}");
        var response = await _blockchainClient.GetDocumentVersionsAsync(request);
        return response.Versions.ToList();
    }

    public async Task<ProjectAnalyticsData> LoadAnalyticsAsync(string projectId)
    {
        var analyticsRequest = new ProjectRequest
        {
            ProjectId = projectId,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? ""
        };
        _readAuthorizer.Apply(analyticsRequest, $"PROJECT:{projectId}:ANALYTICS");
        var analytics = await _blockchainClient.GetAnalyticsAsync(analyticsRequest);

        var auditRequest = new ProjectRequest
        {
            ProjectId = projectId,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? ""
        };
        _readAuthorizer.Apply(auditRequest, $"PROJECT:{projectId}:SECURITY_AUDIT");
        var securityAudit = await _blockchainClient.GetSecurityAuditAsync(auditRequest);

        return new ProjectAnalyticsData(analytics, securityAudit);
    }

    private async Task<Dictionary<string, string>> LoadProjectRolesAsync(IReadOnlyList<string> projects)
    {
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectId in projects.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string role = "None";
            try
            {
                var response = await LoadTasksResponseAsync(projectId);
                role = string.IsNullOrWhiteSpace(response.UserRole) ? "None" : response.UserRole;
            }
            catch
            {
                role = "None";
            }

            roles[projectId] = role;
        }

        return roles;
    }

    private async Task<TaskResponse> LoadTasksResponseAsync(string projectId)
    {
        var request = new ProjectRequest
        {
            ProjectId = projectId,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? ""
        };
        _readAuthorizer.Apply(request, $"PROJECT:{projectId}:TASKS");
        return await _blockchainClient.GetProjectTasksAsync(request);
    }

    private async Task<ChainResponse> LoadChainAsync(string projectId, int count)
    {
        var request = new ChainRequest
        {
            Count = count,
            ChannelId = projectId,
            UserName = _keyService.UserName ?? "Guest",
            UserPublicKey = _keyService.PublicKey ?? "",
            AfterIndex = -1
        };
        _readAuthorizer.Apply(request, $"CHAIN:{projectId}");
        return await _blockchainClient.GetChainAsync(request);
    }

    private async Task<List<string>> LoadProjectMembersAsync(string projectId, string currentUserRole, IReadOnlyList<ProjectTask> tasks)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_keyService.UserName) && currentUserRole != "None")
        {
            members.Add(_keyService.UserName);
        }

        try
        {
            var systemChain = await LoadChainAsync("System", 500);
            foreach (var block in systemChain.Blocks)
            {
                AddProjectMemberFromBlock(projectId, block, members);
            }
        }
        catch
        {
            // Known task participants still provide a useful fallback for assignment lists.
        }

        foreach (var task in tasks)
        {
            AddKnownMember(task.Creator, members);
            AddKnownMember(task.Assignee, members);
        }

        return members
            .Where(member => !string.Equals(member, "None", StringComparison.OrdinalIgnoreCase))
            .OrderBy(member => member, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ProjectTask ToProjectTask(TaskItem task)
    {
        return new ProjectTask
        {
            Id = task.Id,
            Title = task.Title,
            Creator = task.Creator,
            Assignee = task.Assignee,
            Status = (ProjectTaskStatus)task.Status,
            ProjectId = task.ProjectId,
            Description = task.Description,
            ParentTaskId = task.ParentTaskId,
            BranchInfo = task.BranchInfo
        };
    }

    private static List<CommitPayloadUI> BuildCommits(IEnumerable<BlockModel> blocks)
    {
        var commits = new List<CommitPayloadUI>();
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Data)) continue;

            try
            {
                using var doc = JsonDocument.Parse(block.Data);
                var root = doc.RootElement;
                string eventType =
                    root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" :
                    root.TryGetProperty("type", out var lowerTypeProp) ? lowerTypeProp.GetString() ?? "" : "";

                if (eventType == "CodeCommit")
                {
                    commits.Add(new CommitPayloadUI
                    {
                        Repository = root.TryGetProperty("Repository", out var repository) || root.TryGetProperty("repository", out repository) ? repository.GetString() ?? "Unknown" : "Unknown",
                        CommitHash = root.TryGetProperty("CommitHash", out var hash) || root.TryGetProperty("commitHash", out hash) ? hash.GetString() ?? "" : "",
                        Message = root.TryGetProperty("Message", out var message) || root.TryGetProperty("message", out message) ? message.GetString() ?? "" : "",
                        Author = root.TryGetProperty("User", out var user) || root.TryGetProperty("user", out user) ? user.GetString() ?? "Anon" : "Anon",
                        Provider = root.TryGetProperty("Provider", out var provider) ? provider.GetString() ?? "" : "",
                        Branch = root.TryGetProperty("Branch", out var branch) ? branch.GetString() ?? "" : ""
                    });
                }
            }
            catch
            {
                // Skip malformed historical payloads.
            }
        }

        commits.Reverse();
        return commits;
    }

    private static List<ArtifactPayloadUI> BuildArtifacts(IEnumerable<BlockModel> blocks)
    {
        var artifacts = new List<ArtifactPayloadUI>();
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Data)) continue;

            try
            {
                using var doc = JsonDocument.Parse(block.Data);
                if (TryParseArtifact(doc.RootElement, out var artifact))
                {
                    artifacts.Add(artifact);
                }
            }
            catch
            {
                // Skip malformed historical payloads.
            }
        }

        return artifacts;
    }

    private static List<AuditTrailEntry> BuildAuditTrail(IEnumerable<BlockModel> blocks)
    {
        var entries = new List<AuditTrailEntry>();
        foreach (var block in blocks.OrderByDescending(x => x.Index))
        {
            if (TryBuildAuditTrailEntry(block, out var entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static bool TryBuildAuditTrailEntry(BlockModel block, out AuditTrailEntry entry)
    {
        entry = new AuditTrailEntry();
        if (string.IsNullOrWhiteSpace(block.Data))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(block.Data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" :
                root.TryGetProperty("type", out var lowerTypeProp) ? lowerTypeProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            string actor = root.TryGetProperty("User", out var userProp) ? userProp.GetString() ?? "" :
                root.TryGetProperty("user", out var lowerUserProp) ? lowerUserProp.GetString() ?? "" : "";
            string projectId = root.TryGetProperty("ProjectId", out var projectProp) ? projectProp.GetString() ?? "" :
                root.TryGetProperty("projectId", out var lowerProjectProp) ? lowerProjectProp.GetString() ?? "" : "";

            string entityId =
                root.TryGetProperty("TaskId", out var taskId) ? taskId.GetString() ?? "" :
                root.TryGetProperty("DocumentId", out var documentId) ? documentId.GetString() ?? "" :
                root.TryGetProperty("ProposalId", out var proposalId) ? proposalId.GetString() ?? "" :
                root.TryGetProperty("CommitHash", out var commitHash) ? commitHash.GetString() ?? "" :
                root.TryGetProperty("FileHash", out var fileHash) ? fileHash.GetString() ?? "" :
                projectId;

            entry = new AuditTrailEntry
            {
                BlockIndex = block.Index,
                BlockHash = block.Hash,
                Timestamp = block.Timestamp,
                EventType = type,
                Actor = string.IsNullOrWhiteSpace(actor) ? "System" : actor,
                ProjectId = projectId,
                ChannelId = block.ChannelId,
                EntityId = entityId,
                Summary = $"{type}:{entityId}"
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseArtifact(JsonElement root, out ArtifactPayloadUI artifact)
    {
        artifact = new ArtifactPayloadUI();

        string eventType = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";
        string source = root.TryGetProperty("Source", out var sourceProp) ? sourceProp.GetString() ?? "" : "";

        bool isArtifactEvent =
            eventType.Equals("Register", StringComparison.OrdinalIgnoreCase) &&
            (source.Equals("ArtifactRegistry", StringComparison.OrdinalIgnoreCase)
             || source.Equals("UserArtifact", StringComparison.OrdinalIgnoreCase)
             || root.TryGetProperty("FileHash", out _));

        if (!isArtifactEvent)
        {
            return false;
        }

        artifact = new ArtifactPayloadUI
        {
            FileName = root.TryGetProperty("FileName", out var fileName) ? fileName.GetString() ?? "artifact.bin" : "artifact.bin",
            FileHash = root.TryGetProperty("FileHash", out var fileHash) ? fileHash.GetString() ?? "" : "",
            RegisteredBy = root.TryGetProperty("RegisteredBy", out var registeredBy)
                ? registeredBy.GetString() ?? ""
                : root.TryGetProperty("User", out var user) ? user.GetString() ?? "" : "",
            VerificationMethod = root.TryGetProperty("VerificationMethod", out var method) ? method.GetString() ?? "IPFS" : "IPFS",
            SizeBytes = root.TryGetProperty("SizeBytes", out var sizeProp) && sizeProp.TryGetInt64(out var size) ? size : 0,
            ContentType = root.TryGetProperty("ContentType", out var contentType) ? contentType.GetString() ?? "" : "",
            Timestamp = root.TryGetProperty("Timestamp", out var timestamp) ? timestamp.GetString() ?? "" : ""
        };

        return !string.IsNullOrWhiteSpace(artifact.FileHash);
    }

    private static void AddProjectMemberFromBlock(string currentProjectId, BlockModel block, HashSet<string> members)
    {
        if (string.IsNullOrWhiteSpace(block.Data))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(block.Data);
            var root = doc.RootElement;
            string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";
            string projectId = root.TryGetProperty("ProjectId", out var projectProp) ? projectProp.GetString() ?? "" : "";
            if (!string.Equals(projectId, currentProjectId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (type == "CreateProject")
            {
                AddKnownMember(root.TryGetProperty("User", out var userProp) ? userProp.GetString() ?? "" : "", members);
            }
            else if (type == "AssignRole")
            {
                AddKnownMember(root.TryGetProperty("TargetUser", out var targetProp) ? targetProp.GetString() ?? "" : "", members);
            }
        }
        catch
        {
            // Ignore malformed historical blocks.
        }
    }

    private static void AddKnownMember(string? userName, HashSet<string> members)
    {
        string normalized = NormalizeTaskAssignee(userName);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            members.Add(normalized);
        }
    }

    private static string NormalizeTaskAssignee(string? assignee) =>
        string.IsNullOrWhiteSpace(assignee) ||
        string.Equals(assignee.Trim(), "None", StringComparison.OrdinalIgnoreCase)
            ? ""
            : assignee.Trim();
}
