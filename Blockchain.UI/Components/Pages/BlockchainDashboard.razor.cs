using Blockchain.Node;
using Blockchain.UI.Application.Clients;
using Blockchain.UI.Services;
using Blockchain.UI.Components;
using Blockchain.UI.Components.Dashboard;
using Blockchain.UI.Application.UseCases;
using Blockchain.UI.Infrastructure.Browser;
using Blockchain.UI.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blockchain.UI.Components.Pages;

public partial class BlockchainDashboard : ComponentBase, IAsyncDisposable
{
    private static readonly TimeSpan WorkspaceRefreshInterval = TimeSpan.FromSeconds(30);

    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private KeyService MyKeyService { get; set; } = default!;
    [Inject] private IpfsService Ipfs { get; set; } = default!;
    [Inject] private IConfiguration Config { get; set; } = default!;
    [Inject] private IGitIntegrationClient GitIntegrationClient { get; set; } = default!;
    [Inject] private IIpfsIntegrationClient IpfsIntegrationClient { get; set; } = default!;
    [Inject] private IArtifactIntegrationClient ArtifactIntegrationClient { get; set; } = default!;
    [Inject] private IBlockchainRealtimeClient RealtimeClient { get; set; } = default!;
    [Inject] private IProjectWorkspaceDataService WorkspaceDataService { get; set; } = default!;
    [Inject] private IBlockAnchoringService BlockAnchoringService { get; set; } = default!;
    [Inject] private IPeerNetworkClient PeerNetworkClient { get; set; } = default!;
    [Inject] private IClipboardService ClipboardService { get; set; } = default!;
    [Inject] private IBrowserOperationQueue BrowserOperationQueue { get; set; } = default!;
    [Inject] private DashboardActions DashboardActions { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private string currentNodeUrl = "";
    private string nodeUrlInput = "";
    private string nodeConnectionStatus = "";
    private bool isPeerPanelBusy = false;
    private List<PeerNodeInfo> peerUrls = new();
    private string currentNodeRole = "Unknown";
    private PeerDirectorySnapshot? peerDirectorySnapshot;
    private NodeSetupStatus? nodeSetupStatus;
    private NetworkStatus? networkStatus;
    private NetworkInvite? networkInvite;
    private IntentListStatus? intentListStatus;
    private IReadOnlyList<BrowserQueuedOperation> browserQueuedOperations = Array.Empty<BrowserQueuedOperation>();
    private MigrationChecklist? migrationChecklist;
    private UpdateCheckStatus? updateCheckStatus;

    private ActiveTab activeTab = ActiveTab.Board;
    private List<ProjectTask> Tasks = new();
    private List<CommitPayloadUI> Commits = new();
    private List<ArtifactPayloadUI> Artifacts = new();
    private List<BlockModel> chain = new();
    private List<AuditTrailEntry> auditTrail = new();
    private AnalyticsResponse analyticsData = new();
    private SecurityAuditResponse securityAudit = new();
    private List<TaskHistoryItem>? viewingHistory = null;
    private List<GovernanceProposalItem> governanceProposals = new();
    private List<DocumentSummary> documents = new();
    private List<DocumentVersionItem> documentVersions = new();
    private List<string> myProjects = new();
    private List<string> ownedProjects = new();
    private List<string> sharedProjects = new();
    private List<string> projectMembers = new();
    private Dictionary<string, string> projectRoles = new(StringComparer.OrdinalIgnoreCase);
    private string currentUserRole = "None";
    private HashSet<string> implementedProposalIds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> locallyImplementedProposalIds = new(StringComparer.OrdinalIgnoreCase);
    private IntegrationStatusUI gitIntegrationStatus = new() { Name = "Git webhook" };
    private IntegrationStatusUI ipfsIntegrationStatus = new() { Name = "IPFS node" };

    private string? newUserPassword;
    private bool showPasswordEntry = false;
    private string passkeyModalMessage = "";

    private string newProjectName = "";
    private string newMemberName = "";
    private string newMemberRole = "Developer";
    private string newMemberPublicKey = "";
    private string newMemberPublicKeyFingerprint = "";
    private string newProposalTitle = "";
    private string newProposalDescription = "";
    private string selectedDocumentId = "";
    private string documentTitle = "";
    private string documentContent = "";
    private bool isDocumentDraftDirty = false;
    private bool isGitRepositoryConnecting = false;

    private string CurrentProjectId = "System";
    private string userNameInput = "";
    private string restoreMnemonic = "";
    private string statusMessage = "";
    private string unlockPassword = "";
    private string unlockMessage = "";
    private bool isRestoreMode = false;
    private bool isUploading = false;
    private bool isMining = false;
    private bool isSyncing = false;
    private bool isBrowserQueueRetrying = false;
    private string workspaceSnapshotKey = "";
    private CancellationTokenSource? workspaceRefreshCts;
    private string mnemonicCopyButtonText = "Copy to Clipboard";

    private bool isIssueModalOpen = false;
    private bool isNewTask = true;
    private ProjectTask editingTask = new();
    private bool isGenesisModeActive = false;
    private bool CanCreateTask => MyKeyService.IsLoggedIn && myProjects.Count > 0 && !string.IsNullOrWhiteSpace(CurrentProjectId) && CurrentProjectId != "System";
    private bool CanManageMembers => string.Equals(CurrentUserRoleLabel, "Owner", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(CurrentUserRoleLabel, "Manager", StringComparison.OrdinalIgnoreCase);
    private string CurrentUserRoleLabel => string.IsNullOrWhiteSpace(currentUserRole) ? "None" : currentUserRole;
    private bool IsCurrentUserOwner => string.Equals(CurrentUserRoleLabel, "Owner", StringComparison.OrdinalIgnoreCase);
    private string IpfsGatewayUrl => (Config["IpfsGatewayUrl"] ?? "http://127.0.0.1:8080/ipfs").Trim().TrimEnd('/');
    private int TodoTaskCount => Tasks.Count(x => x.Status == ProjectTaskStatus.Todo);
    private int InProgressTaskCount => Tasks.Count(x => x.Status == ProjectTaskStatus.InProgress);
    private int DoneTaskCount => Tasks.Count(x => x.Status == ProjectTaskStatus.Done);
    private int OpenTaskCount => TodoTaskCount + InProgressTaskCount;

    private Task OnNewProjectNameChanged(string value) { newProjectName = value; return Task.CompletedTask; }
    private Task OnUserNameInputChanged(string value) { userNameInput = value; return Task.CompletedTask; }
    private Task OnRestoreMnemonicChanged(string value) { restoreMnemonic = value; return Task.CompletedTask; }
    private Task OnNewUserPasswordChanged(string value) { newUserPassword = value; return Task.CompletedTask; }
    private Task OnUnlockPasswordChanged(ChangeEventArgs e) { unlockPassword = e.Value?.ToString() ?? ""; return Task.CompletedTask; }
    private Task OnNewMemberNameChanged(string value) { newMemberName = value; return Task.CompletedTask; }
    private Task OnNewMemberRoleChanged(string value) { newMemberRole = value; return Task.CompletedTask; }
    private Task OnNewMemberPublicKeyChanged(string value) { newMemberPublicKey = value; return Task.CompletedTask; }
    private Task OnNewMemberPublicKeyFingerprintChanged(string value) { newMemberPublicKeyFingerprint = value; return Task.CompletedTask; }
    private Task OnNewProposalTitleChanged(string value) { newProposalTitle = value; return Task.CompletedTask; }
    private Task OnNewProposalDescriptionChanged(string value) { newProposalDescription = value; return Task.CompletedTask; }
    private Task OnDocumentTitleChanged(string value) { documentTitle = value; isDocumentDraftDirty = true; return Task.CompletedTask; }
    private Task OnDocumentContentChanged(string value) { documentContent = value; isDocumentDraftDirty = true; return Task.CompletedTask; }
    private Task OnNodeUrlInputChanged(string value) { nodeUrlInput = value; return Task.CompletedTask; }
    private Task CancelRestoreMode() { isRestoreMode = false; return Task.CompletedTask; }
    private string MnemonicCopyButtonText => mnemonicCopyButtonText;


    protected override async Task OnInitializedAsync()
    {
        isGenesisModeActive = bool.TryParse(Config["GenesisModeEnabled"], out var genesis) && genesis;

        currentNodeUrl = ResolveNodeUrlFromQuery(DashboardActions.GetInitialNodeUrl());
        nodeUrlInput = currentNodeUrl;
        try
        {
            var setupState = await PeerNetworkClient.GetSetupStateAsync(currentNodeUrl);
            if (setupState is { FullNodeConfigured: false } &&
                string.Equals(setupState.State, "NotConfigured", StringComparison.OrdinalIgnoreCase))
            {
                Navigation.NavigateTo($"/setup?nodeUrl={Uri.EscapeDataString(currentNodeUrl)}");
                return;
            }

            await StartRealtimeConnection();
            browserQueuedOperations = await BrowserOperationQueue.LoadAsync();
            await RefreshPeers();
            await LoadIntegrationStatus();
        }
        catch (Exception ex)
        {
            nodeConnectionStatus = $"Node unavailable: {ex.Message}";
        }
    }

    private static bool IsValidPublicKey(string publicKeyBase64)
    {
        try
        {
            byte[] raw = Convert.FromBase64String(publicKeyBase64);
            return raw.Length >= 64;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputePublicKeyFingerprint(string publicKeyBase64)
    {
        byte[] raw = Convert.FromBase64String(publicKeyBase64);
        byte[] hash = SHA256.HashData(raw);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSimpleHash(string data)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task CopyMyPublicKey()
    {
        if (string.IsNullOrWhiteSpace(MyKeyService.PublicKey))
        {
            statusMessage = "No public key available.";
            return;
        }

        var result = await ClipboardService.CopyTextAsync(MyKeyService.PublicKey);
        statusMessage = result.Success ? "Public key copied to clipboard." : result.Error;
    }

    private async Task StartRealtimeConnection()
    {
        await RealtimeClient.ConnectAsync(currentNodeUrl, async block =>
        {
            if (block.ChannelId == CurrentProjectId || block.ChannelId == "System")
            {
                var refreshHints = ApplyBlockToUI(block);
                if (refreshHints.RefreshMembership)
                {
                    await SyncChain();
                }
                else if (refreshHints.RefreshGovernance && activeTab == ActiveTab.Governance)
                {
                    await RefreshGovernance();
                }
                await InvokeAsync(StateHasChanged);
            }
        });

        nodeConnectionStatus = $"Connected to {currentNodeUrl}.";

        if (MyKeyService.IsLoggedIn)
        {
            await JoinSignalRGroups();
        }
    }

    private async Task ConnectToNode()
    {
        var validation = DashboardActions.ValidateNodeUrl(nodeUrlInput);
        if (!validation.Success || string.IsNullOrWhiteSpace(validation.Value))
        {
            nodeConnectionStatus = validation.Error;
            return;
        }

        try
        {
            isPeerPanelBusy = true;
            string nextUrl = validation.Value;
            currentNodeUrl = nextUrl;
            nodeUrlInput = currentNodeUrl;

            await StartRealtimeConnection();
            await RefreshPeers();
            await LoadIntegrationStatus();

            if (MyKeyService.IsLoggedIn)
            {
                await SyncChain();
            }

            statusMessage = $"UI connected to {currentNodeUrl}.";
        }
        catch (Exception ex)
        {
            nodeConnectionStatus = $"Connection failed: {ex.Message}";
            statusMessage = nodeConnectionStatus;
        }
        finally
        {
            isPeerPanelBusy = false;
            StateHasChanged();
        }
    }

    private async Task RefreshPeers()
    {
        try
        {
            isPeerPanelBusy = true;
            var result = await DashboardActions.LoadPeersAsync(currentNodeUrl);
            if (!result.Success || result.Value == null)
            {
                peerUrls.Clear();
                peerDirectorySnapshot = null;
                nodeSetupStatus = null;
                networkStatus = null;
                intentListStatus = null;
                migrationChecklist = null;
                updateCheckStatus = null;
                nodeConnectionStatus = result.Error;
                return;
            }

            currentNodeRole = result.Value.Directory.CurrentRole;
            peerDirectorySnapshot = result.Value.Directory;
            nodeSetupStatus = result.Value.SetupStatus;
            networkStatus = result.Value.NetworkStatus;
            networkInvite = result.Value.NetworkInvite;
            intentListStatus = result.Value.IntentList;
            migrationChecklist = result.Value.MigrationChecklist;
            updateCheckStatus = result.Value.UpdateCheck;
            peerUrls = result.Value.Directory.Peers.ToList();
            nodeConnectionStatus = networkStatus == null
                ? $"Connected to {currentNodeUrl}. {peerUrls.Count} peer(s) registered."
                : $"Connected to {currentNodeUrl}. {networkStatus.NodeRole} node, {peerUrls.Count} peer(s), {networkStatus.Channels.Count} channel(s).";
            await RetryBrowserQueueIfConnected();
        }
        catch (Exception ex)
        {
            peerUrls.Clear();
            peerDirectorySnapshot = null;
            nodeSetupStatus = null;
            networkStatus = null;
            networkInvite = null;
            intentListStatus = null;
            migrationChecklist = null;
            updateCheckStatus = null;
            currentNodeRole = "Unknown";
            nodeConnectionStatus = $"Node unavailable: {ex.Message}";
        }
        finally
        {
            isPeerPanelBusy = false;
            StateHasChanged();
        }
    }

    private async Task LoadNetwork()
    {
        activeTab = ActiveTab.Network;
        await RefreshPeers();
    }

    private async Task<SetupPlanResponse?> CreateSetupPlan(SetupPlanRequest request)
    {
        var validation = DashboardActions.ValidateNodeUrl(currentNodeUrl);
        if (!validation.Success || string.IsNullOrWhiteSpace(validation.Value))
        {
            statusMessage = validation.Error;
            return null;
        }

        try
        {
            isPeerPanelBusy = true;
            var response = await DashboardActions.CreateSetupPlanAsync(validation.Value, request);
            if (response?.Valid == true && !string.IsNullOrWhiteSpace(response.FileName))
            {
                await JS.InvokeVoidAsync("downloadTextFile", response.FileName, response.EnvContent);
                statusMessage = $"Generated {response.FileName}.";
            }
            else
            {
                statusMessage = response?.Messages.Count > 0 ? string.Join(" ", response.Messages) : "Setup plan failed.";
            }

            return response;
        }
        catch (Exception ex)
        {
            statusMessage = $"Setup plan failed: {ex.Message}";
            return null;
        }
        finally
        {
            isPeerPanelBusy = false;
            StateHasChanged();
        }
    }

    private async Task<PeerTrustResponse?> SetPeerTrust(PeerTrustRequest request)
    {
        var validation = DashboardActions.ValidateNodeUrl(currentNodeUrl);
        if (!validation.Success || string.IsNullOrWhiteSpace(validation.Value))
        {
            statusMessage = validation.Error;
            return null;
        }

        try
        {
            isPeerPanelBusy = true;
            var response = await DashboardActions.SetPeerTrustAsync(validation.Value, request);
            statusMessage = response?.Message ?? "Peer trust update failed.";
            await RefreshPeers();
            return response;
        }
        catch (Exception ex)
        {
            statusMessage = $"Peer trust update failed: {ex.Message}";
            return null;
        }
        finally
        {
            isPeerPanelBusy = false;
            StateHasChanged();
        }
    }

    private async Task<PeerRoleResponse?> SetPeerRole(PeerRoleRequest request)
    {
        var validation = DashboardActions.ValidateNodeUrl(currentNodeUrl);
        if (!validation.Success || string.IsNullOrWhiteSpace(validation.Value))
        {
            statusMessage = validation.Error;
            return null;
        }

        try
        {
            isPeerPanelBusy = true;
            var response = await DashboardActions.SetPeerRoleAsync(validation.Value, request);
            statusMessage = response?.Message ?? "Peer role update failed.";
            await RefreshPeers();
            return response;
        }
        catch (Exception ex)
        {
            statusMessage = $"Peer role update failed: {ex.Message}";
            return null;
        }
        finally
        {
            isPeerPanelBusy = false;
            StateHasChanged();
        }
    }

    private async Task<EdgeSyncStatus?> SyncNetwork(NetworkSyncRequest request)
    {
        var validation = DashboardActions.ValidateNodeUrl(currentNodeUrl);
        if (!validation.Success || string.IsNullOrWhiteSpace(validation.Value))
        {
            statusMessage = validation.Error;
            return null;
        }

        try
        {
            isPeerPanelBusy = true;
            var response = await DashboardActions.SyncNetworkAsync(validation.Value, request);
            statusMessage = response == null
                ? "Network sync failed."
                : $"Network sync: {response.State}, applied {response.LastAppliedBlocks} block(s).";
            await RefreshPeers();
            return response;
        }
        catch (Exception ex)
        {
            statusMessage = $"Network sync failed: {ex.Message}";
            return null;
        }
        finally
        {
            isPeerPanelBusy = false;
            StateHasChanged();
        }
    }

    private async Task LoadArtifacts()
    {
        activeTab = ActiveTab.Artifacts;
        await LoadIntegrationStatus();
    }

    private async Task LoadIntegrationStatus()
    {
        await LoadGitIntegrationStatus();
        await LoadIpfsIntegrationStatus();
    }

    private async Task LoadGitIntegrationStatus()
    {
        gitIntegrationStatus = await GitIntegrationClient.GetStatusAsync(currentNodeUrl);
    }

    private async Task LoadIpfsIntegrationStatus()
    {
        ipfsIntegrationStatus = await IpfsIntegrationClient.GetStatusAsync(currentNodeUrl);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await MyKeyService.LoadFromStorage();

            if (MyKeyService.IsLoggedIn)
            {
                await JoinSignalRGroups();
                await SyncChain();
                StartWorkspaceRefreshLoop();
            }
            else
            {
                StateHasChanged();
            }
        }
    }

    private async Task JoinSignalRGroups()
    {
        if (MyKeyService.IsLoggedIn)
        {
            try
            {
                bool joined = await RealtimeClient.JoinProjectsAsync(CurrentProjectId);
                if (!joined)
                {
                    statusMessage = "Realtime auth failed. User group subscription denied.";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SignalR] Error subscribing to channel: {ex.Message}");
            }
        }
    }

    private (bool RefreshMembership, bool RefreshGovernance) ApplyBlockToUI(BlockModel block)
    {
        if (string.IsNullOrWhiteSpace(block.Data)) return (false, false);
        if (chain.Any(existing => IsSameBlock(existing, block)))
        {
            return (false, false);
        }

        bool shouldRefreshMembership = false;
        bool shouldRefreshGovernance = false;
        try
        {
            using var doc = JsonDocument.Parse(block.Data);
            var root = doc.RootElement;
            string type = root.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";
            if (type == "CreateProposal" || type == "CastVote")
            {
                shouldRefreshGovernance = true;
            }

            if (type == "CodeCommit")
            {
                Commits.Insert(0, new CommitPayloadUI
                    {
                        Repository = root.TryGetProperty("Repository", out var r) || root.TryGetProperty("repository", out r) ? r.GetString() ?? "Unknown" : "Unknown",
                        CommitHash = root.TryGetProperty("CommitHash", out var h) || root.TryGetProperty("commitHash", out h) ? h.GetString() ?? "" : "",
                        Message = root.TryGetProperty("Message", out var m) || root.TryGetProperty("message", out m) ? m.GetString() ?? "" : "",
                        Author = root.TryGetProperty("User", out var u) || root.TryGetProperty("user", out u) ? u.GetString() ?? "Anon" : "Anon",
                        Provider = root.TryGetProperty("Provider", out var provider) ? provider.GetString() ?? "" : "",
                        Branch = root.TryGetProperty("Branch", out var branch) ? branch.GetString() ?? "" : ""
                    });
            }
            else if (type == "Create")
            {
                string tId = root.TryGetProperty("TaskId", out var idProp) ? idProp.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(tId) && !Tasks.Any(x => x.Id == tId))
                {
                    Tasks.Add(new ProjectTask
                        {
                            Id = tId,
                            Title = root.TryGetProperty("Title", out var tProp) ? tProp.GetString() ?? "" : "",
                            Assignee = root.TryGetProperty("Assignee", out var aProp) ? aProp.GetString() ?? "" : "",
                            ProjectId = root.TryGetProperty("ProjectId", out var pProp) ? pProp.GetString() ?? "" : "",
                            Status = root.TryGetProperty("Status", out var sProp) ? (ProjectTaskStatus)sProp.GetInt32() : ProjectTaskStatus.Todo,
                            ParentTaskId = root.TryGetProperty("ParentTaskId", out var parentProp) ? parentProp.GetString() ?? "" : ""
                        });
                    UpdateImplementedProposalIds();
                }
            }
            else if (type == "Move" || type == "Update")
            {
                string tId = root.TryGetProperty("TaskId", out var idProp) ? idProp.GetString() ?? "" : "";
                var task = Tasks.FirstOrDefault(x => x.Id == tId);
                if (task != null)
                {
                    task.Status = root.TryGetProperty("Status", out var sProp) ? (ProjectTaskStatus)sProp.GetInt32() : task.Status;
                }
            }
            else if (TryParseArtifactFromJson(root, out var artifact))
            {
                if (!Artifacts.Any(a => a.FileHash == artifact.FileHash))
                {
                    Artifacts.Insert(0, artifact);
                }
            }

            if (type == "AssignRole")
            {
                string targetUser = root.TryGetProperty("TargetUser", out var tu) ? tu.GetString() ?? "" : "";
                string projectId = root.TryGetProperty("ProjectId", out var p) ? p.GetString() ?? "" : "";
                if (string.Equals(projectId, CurrentProjectId, StringComparison.OrdinalIgnoreCase))
                {
                    string normalizedTarget = NormalizeTaskAssignee(targetUser);
                    if (!string.IsNullOrWhiteSpace(normalizedTarget) &&
                        !projectMembers.Contains(normalizedTarget, StringComparer.OrdinalIgnoreCase))
                    {
                        projectMembers.Add(normalizedTarget);
                        projectMembers = projectMembers.OrderBy(member => member, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }

                string currentUser = MyKeyService.UserName ?? "";
                if (!string.IsNullOrWhiteSpace(currentUser) &&
                    string.Equals(targetUser, currentUser, StringComparison.OrdinalIgnoreCase))
                {
                    shouldRefreshMembership = true;
                }
            }

            chain.Add(block);
            workspaceSnapshotKey = "";
            if (TryBuildAuditTrailEntry(block, out var entry))
            {
                if (!auditTrail.Any(existing => existing.BlockIndex == entry.BlockIndex &&
                                                string.Equals(existing.BlockHash, entry.BlockHash, StringComparison.OrdinalIgnoreCase)))
                {
                    auditTrail.Insert(0, entry);
                }

                if (auditTrail.Count > 200)
                {
                    auditTrail = auditTrail.Take(200).ToList();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApplyBlockToUI] JSON parsing error: {ex.Message}");
        }
        return (shouldRefreshMembership, shouldRefreshGovernance);
    }

    private async Task OnProjectChanged(ChangeEventArgs e)
    {
        CurrentProjectId = e.Value?.ToString() ?? "System";
        selectedDocumentId = "";
        documentTitle = "";
        documentContent = "";
        documentVersions.Clear();
        await JoinSignalRGroups();
        await SyncChain();
    }

    private async Task InviteMember()
    {
        if (!CanManageMembers)
        {
            statusMessage = "Only Owner or Manager can grant access in this project.";
            return;
        }

        if (string.IsNullOrWhiteSpace(newMemberName) || string.IsNullOrWhiteSpace(newMemberPublicKey)) return;
        if (!IsValidPublicKey(newMemberPublicKey))
        {
            statusMessage = "Invalid member public key format.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(newMemberPublicKeyFingerprint))
        {
            string expectedFingerprint = newMemberPublicKeyFingerprint.Trim().ToLowerInvariant();
            string actualFingerprint = ComputePublicKeyFingerprint(newMemberPublicKey);
            if (!string.Equals(expectedFingerprint, actualFingerprint, StringComparison.Ordinal))
            {
                statusMessage = "Member key fingerprint does not match the provided public key.";
                return;
            }
        }

        string payloadJson = new JsonObject
        {
            ["Type"] = "AssignRole",
            ["User"] = MyKeyService.UserName,
            ["ProjectId"] = CurrentProjectId,
            ["TargetUser"] = newMemberName,
            ["TargetPublicKey"] = newMemberPublicKey,
            ["Role"] = newMemberRole,
            ["Timestamp"] = DateTime.UtcNow.ToString("O")
        }.ToJsonString();

        await SendJsonBlockJson(payloadJson, "System");
        newMemberName = "";
        newMemberPublicKey = "";
        newMemberPublicKeyFingerprint = "";
    }

    private string FormatJson(string json) { try { using var doc = JsonDocument.Parse(json); return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }); } catch { return json; } }

    private async Task CreateWallet()
    {
        try
        {
            string requestedUserName = userNameInput.Trim();
            if (string.IsNullOrWhiteSpace(requestedUserName))
            {
                statusMessage = "Enter a user name before creating a wallet.";
                return;
            }

            var availability = await BlockAnchoringService.CheckUserNameAvailabilityAsync(requestedUserName);
            if (!availability.Success)
            {
                statusMessage = availability.Message;
                return;
            }

            if (availability.Exists)
            {
                statusMessage = $"User name '{requestedUserName}' is already taken. Restore the original wallet for this name or choose another name.";
                return;
            }

            await Logout();
            await MyKeyService.CreateNewWallet(requestedUserName);
            showPasswordEntry = true;
            statusMessage = $"Wallet created for '{requestedUserName}'. Save the wallet file before continuing.";
            await JoinSignalRGroups();
            await SyncChain();
            StartWorkspaceRefreshLoop();
        }
        catch (Exception ex)
        {
            statusMessage = $"Wallet creation failed: {ex.Message}";
        }
    }

    private void RestoreMode() => isRestoreMode = !isRestoreMode;

    private async Task RestoreWallet()
    {
        string requestedUserName = userNameInput.Trim();
        if (string.IsNullOrWhiteSpace(requestedUserName))
        {
            statusMessage = "Enter the user name that belongs to this wallet.";
            return;
        }

        if (await MyKeyService.RestoreWallet(restoreMnemonic, requestedUserName))
        {
            var identity = await BlockAnchoringService.CheckCurrentUserIdentityAsync();
            if (!identity.Success)
            {
                await Logout();
                statusMessage = $"Identity check failed: {identity.Message}";
                return;
            }

            if (identity.Exists && !identity.PublicKeyMatches)
            {
                await Logout();
                statusMessage = $"Wallet key does not match user name '{requestedUserName}'. Restore the correct wallet or use another user name.";
                return;
            }

            isRestoreMode = false;
            statusMessage = identity.Exists
                ? $"Wallet restored for '{requestedUserName}'."
                : $"Wallet restored. User name '{requestedUserName}' is not registered yet.";
            await JoinSignalRGroups();
            await SyncChain();
            StartWorkspaceRefreshLoop();
        }
        else
        {
            statusMessage = "Recovery phrase is invalid.";
        }
    }

    private async Task CopySecretPhraseToClipboard()
    {
        if (string.IsNullOrWhiteSpace(MyKeyService.CurrentMnemonic))
        {
            statusMessage = "Secret phrase is unavailable.";
            return;
        }

        var result = await ClipboardService.CopyTextAsync(MyKeyService.CurrentMnemonic);
        if (result.Success)
        {
            mnemonicCopyButtonText = "Copied!";
            statusMessage = "Secret phrase copied to clipboard.";
            StateHasChanged();
            await Task.Delay(1800);
            mnemonicCopyButtonText = "Copy to Clipboard";
            StateHasChanged();
        }
        else
        {
            statusMessage = result.Error;
        }
    }
    private async Task Logout()
    {
        await MyKeyService.Logout();
        CurrentProjectId = "System";
        myProjects.Clear();
        ownedProjects.Clear();
        sharedProjects.Clear();
        projectRoles.Clear();
        currentUserRole = "None";
        Tasks.Clear();
        chain.Clear();
        Commits.Clear();
        Artifacts.Clear();
        auditTrail.Clear();
        documents.Clear();
        documentVersions.Clear();
        governanceProposals.Clear();
        showPasswordEntry = false;
        isRestoreMode = false;
        statusMessage = "Signed out.";
        StopWorkspaceRefreshLoop();
        StateHasChanged();
    }

    private async Task UnlockWalletWithPassword()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(unlockPassword))
            {
                unlockMessage = "Enter wallet password.";
                return;
            }

            await MyKeyService.UnlockSessionWithPasswordAsync(unlockPassword);
            unlockPassword = "";
            unlockMessage = "";
            await JoinSignalRGroups();
            await SyncChain();
            StartWorkspaceRefreshLoop();
        }
        catch (Exception ex)
        {
            unlockMessage = ex.Message;
        }
    }

    private async Task UnlockWalletWithPasskey()
    {
        try
        {
            await MyKeyService.UnlockSessionWithPasskeyAsync();
            unlockPassword = "";
            unlockMessage = "";
            await JoinSignalRGroups();
            await SyncChain();
            StartWorkspaceRefreshLoop();
        }
        catch (Exception ex)
        {
            unlockMessage = ex.Message;
        }
    }

    private void OpenTaskModal(ProjectTask? task)
    {
        if (task == null && !CanCreateTask)
        {
            statusMessage = "Create or select a project before adding work items.";
            return;
        }

        if (task == null)
        {
            isNewTask = true;
            editingTask = new ProjectTask
                {
                    Id = "TSK-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper(),
                    ProjectId = CurrentProjectId ?? "System",
                    Status = ProjectTaskStatus.Todo
                };
        }
        else
        {
            isNewTask = false;
            editingTask = new ProjectTask
                {
                    Id = task.Id,
                    Title = task.Title,
                    Description = task.Description,
                    Assignee = NormalizeTaskAssignee(task.Assignee),
                    Status = task.Status,
                    ProjectId = task.ProjectId
                };
        }
        isIssueModalOpen = true;
    }

    private void UpdateImplementedProposalIds()
    {
        implementedProposalIds = Tasks
            .Select(task => task.ParentTaskId)
            .Where(parentId => !string.IsNullOrWhiteSpace(parentId) && parentId.StartsWith("GOV-", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        implementedProposalIds.UnionWith(locallyImplementedProposalIds);
    }

    private Task CreateTaskFromProposal(string proposalId)
    {
        if (!CanCreateTask || string.IsNullOrWhiteSpace(proposalId))
        {
            return Task.CompletedTask;
        }

        GovernanceProposalItem? proposal = governanceProposals.FirstOrDefault(item =>
            string.Equals(item.ProposalId, proposalId, StringComparison.OrdinalIgnoreCase));

        if (proposal == null || !string.Equals(proposal.Status, "Approved", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        if (implementedProposalIds.Contains(proposal.ProposalId))
        {
            statusMessage = "A task has already been created from this proposal.";
            return Task.CompletedTask;
        }

        isNewTask = true;
        editingTask = new ProjectTask
        {
            Id = "TSK-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant(),
            ProjectId = CurrentProjectId ?? "System",
            Title = proposal.Title,
            Description = string.IsNullOrWhiteSpace(proposal.Description)
                ? $"Created from accepted proposal {proposal.ProposalId}."
                : proposal.Description,
            Status = ProjectTaskStatus.Todo,
            ParentTaskId = proposal.ProposalId
        };

        isIssueModalOpen = true;
        return Task.CompletedTask;
    }

    private async Task ShowHistory(string taskId)
    {
        viewingHistory = new List<TaskHistoryItem>();
        try
        {
            viewingHistory = (await WorkspaceDataService.LoadTaskHistoryAsync(CurrentProjectId, taskId)).ToList();
        }
        catch (Exception ex) { statusMessage = "History Error: " + ex.Message; viewingHistory = null; }
    }

    private async Task LoadGovernance()
    {
        activeTab = ActiveTab.Governance;
        await RefreshGovernance();
    }

    private async Task RefreshGovernance()
    {
        if (!CanCreateTask)
        {
            governanceProposals = new List<GovernanceProposalItem>();
            return;
        }

        try
        {
            governanceProposals = (await WorkspaceDataService.LoadGovernanceAsync(CurrentProjectId)).ToList();
        }
        catch (Exception ex)
        {
            statusMessage = "Governance Error: " + ex.Message;
        }
    }

    private async Task CreateProposal()
    {
        if (!CanCreateTask || string.IsNullOrWhiteSpace(newProposalTitle)) return;

        string payloadJson = new JsonObject
        {
            ["Type"] = "CreateProposal",
            ["ProposalId"] = "GOV-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper(),
            ["Title"] = newProposalTitle.Trim(),
            ["Description"] = newProposalDescription.Trim(),
            ["User"] = MyKeyService.UserName,
            ["ProjectId"] = CurrentProjectId,
            ["Timestamp"] = DateTime.UtcNow.ToString("O")
        }.ToJsonString();

        bool ok = await SendJsonBlockJson(payloadJson, CurrentProjectId);
        if (ok)
        {
            newProposalTitle = "";
            newProposalDescription = "";
            await RefreshGovernance();
        }
    }

    private async Task CastVote((string ProposalId, bool VoteYes) args)
    {
        if (!CanCreateTask || string.IsNullOrWhiteSpace(args.ProposalId)) return;

        string payloadJson = new JsonObject
        {
            ["Type"] = "CastVote",
            ["ProposalId"] = args.ProposalId,
            ["Vote"] = args.VoteYes,
            ["User"] = MyKeyService.UserName,
            ["ProjectId"] = CurrentProjectId,
            ["Timestamp"] = DateTime.UtcNow.ToString("O")
        }.ToJsonString();

        bool ok = await SendJsonBlockJson(payloadJson, CurrentProjectId);
        if (ok)
        {
            await RefreshGovernance();
        }
    }

    private async Task LoadDocuments()
    {
        activeTab = ActiveTab.Documents;
        await RefreshDocuments();
    }

    private async Task RefreshDocuments()
    {
        await RefreshDocuments(preserveEditorDraft: false);
    }

    private async Task RefreshDocuments(bool preserveEditorDraft)
    {
        if (!CanCreateTask)
        {
            documents = new List<DocumentSummary>();
            documentVersions = new List<DocumentVersionItem>();
            selectedDocumentId = "";
            documentTitle = "";
            documentContent = "";
            isDocumentDraftDirty = false;
            return;
        }

        try
        {
            string previousSelectedDocumentId = selectedDocumentId;
            string previousDocumentTitle = documentTitle;
            string previousDocumentContent = documentContent;

            var result = await WorkspaceDataService.LoadDocumentsAsync(CurrentProjectId, selectedDocumentId);
            documents = result.Documents.ToList();
            documentVersions = result.Versions.ToList();

            if (preserveEditorDraft)
            {
                selectedDocumentId = previousSelectedDocumentId;
                documentTitle = previousDocumentTitle;
                documentContent = previousDocumentContent;
                return;
            }

            selectedDocumentId = result.SelectedDocumentId;
            documentTitle = result.DocumentTitle;
            documentContent = result.DocumentContent;
            isDocumentDraftDirty = false;
        }
        catch (Exception ex)
        {
            statusMessage = "Document Error: " + ex.Message;
        }
    }

    private async Task SelectDocument(string documentId)
    {
        isDocumentDraftDirty = false;
        selectedDocumentId = documentId;
        var selected = documents.FirstOrDefault(d => d.DocumentId == documentId);
        if (selected != null)
        {
            documentTitle = selected.Title;
            documentContent = selected.Content;
        }

        documentVersions = (await WorkspaceDataService.LoadDocumentVersionsAsync(CurrentProjectId, documentId)).ToList();
    }

    private async Task LoadDocumentVersions(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId)) return;

        documentVersions = (await WorkspaceDataService.LoadDocumentVersionsAsync(CurrentProjectId, documentId)).ToList();
    }

    private async Task SaveDocument()
    {
        if (!CanCreateTask || string.IsNullOrWhiteSpace(documentTitle)) return;

        bool isNewDocument = string.IsNullOrWhiteSpace(selectedDocumentId);
        string documentId = isNewDocument
            ? "DOC-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant()
            : selectedDocumentId;

        string payloadJson = new JsonObject
        {
            ["Type"] = isNewDocument ? "CreateDocument" : "UpdateDocument",
            ["DocumentId"] = documentId,
            ["Title"] = documentTitle.Trim(),
            ["Content"] = documentContent ?? "",
            ["User"] = MyKeyService.UserName,
            ["ProjectId"] = CurrentProjectId,
            ["Timestamp"] = DateTime.UtcNow.ToString("O")
        }.ToJsonString();

        bool ok = await SendJsonBlockJson(payloadJson, CurrentProjectId);
        if (ok)
        {
            selectedDocumentId = documentId;
            isDocumentDraftDirty = false;
            await RefreshDocuments();
            statusMessage = isNewDocument ? "Document created and anchored." : "New document version anchored.";
        }
    }

    private async Task LoadAnalytics()
    {
        activeTab = ActiveTab.Analytics;
        try
        {
            var result = await WorkspaceDataService.LoadAnalyticsAsync(CurrentProjectId);
            analyticsData = result.Analytics;
            securityAudit = result.SecurityAudit;
        }
        catch (Exception ex)
        {
            statusMessage = ex.Message;
        }
    }

    private async Task<bool> SendJsonBlockJson(string payloadJson, string targetChannel)
    {
        try
        {
            isMining = true;
            StateHasChanged();

            var result = await BlockAnchoringService.AnchorJsonStringAsync(payloadJson, targetChannel);
            statusMessage = result.Message;

            if (result.Success) await SyncChain();
            else if (ShouldQueueBrowserOperation(result.Message))
            {
                await BrowserOperationQueue.EnqueueAsync(ExtractPayloadType(payloadJson), targetChannel, payloadJson, result.Message);
                browserQueuedOperations = await BrowserOperationQueue.LoadAsync();
                statusMessage = "Action queued locally. It will be submitted when this browser can reach the local node.";
            }

            return result.Success;
        }
        catch (Exception ex)
        {
            await BrowserOperationQueue.EnqueueAsync(ExtractPayloadType(payloadJson), targetChannel, payloadJson, ex.Message);
            browserQueuedOperations = await BrowserOperationQueue.LoadAsync();
            statusMessage = "Action queued locally. It will be submitted when this browser can reach the local node.";
            return false;
        }
        finally { isMining = false; StateHasChanged(); }
    }

    private static bool ShouldQueueBrowserOperation(string message) =>
        message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("endpoint", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("network", StringComparison.OrdinalIgnoreCase);

    private static string ExtractPayloadType(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("Type", out var type) ||
                doc.RootElement.TryGetProperty("type", out type))
            {
                return type.GetString() ?? "Operation";
            }
        }
        catch
        {
        }

        return "Operation";
    }

    private async Task RetryBrowserQueue()
    {
        await RetryBrowserQueueCore(showNoopMessage: true);
    }

    private async Task RetryBrowserQueueIfConnected()
    {
        if (!MyKeyService.IsLoggedIn || isBrowserQueueRetrying)
        {
            return;
        }

        var queued = await BrowserOperationQueue.LoadAsync();
        if (queued.Count == 0)
        {
            browserQueuedOperations = queued;
            return;
        }

        await RetryBrowserQueueCore(showNoopMessage: false);
    }

    private async Task RetryBrowserQueueCore(bool showNoopMessage)
    {
        if (isBrowserQueueRetrying)
        {
            return;
        }

        isBrowserQueueRetrying = true;
        var queued = await BrowserOperationQueue.LoadAsync();
        int committed = 0;
        try
        {
            foreach (var item in queued)
            {
                var result = await BlockAnchoringService.AnchorJsonStringAsync(item.PayloadJson, item.ChannelId);
                if (result.Success)
                {
                    committed++;
                    await BrowserOperationQueue.RemoveAsync(item.OperationId);
                }
            }

            browserQueuedOperations = await BrowserOperationQueue.LoadAsync();
            if (committed > 0)
            {
                statusMessage = $"Submitted {committed} queued action(s).";
            }
            else if (showNoopMessage)
            {
                statusMessage = "Queued actions are still waiting for the local node.";
            }
        }
        finally
        {
            isBrowserQueueRetrying = false;
        }
    }

    private async Task ClearBrowserQueue()
    {
        await BrowserOperationQueue.ClearCommittedAsync();
        browserQueuedOperations = await BrowserOperationQueue.LoadAsync();
        statusMessage = "Submitted browser queue entries cleared.";
    }

    private async Task SyncChain()
    {
        if (isSyncing)
        {
            return;
        }

        try
        {
            isSyncing = true;
            var workspace = await WorkspaceDataService.LoadAsync(CurrentProjectId);
            string nextSnapshotKey = BuildWorkspaceSnapshotKey(workspace);
            if (!string.IsNullOrEmpty(workspaceSnapshotKey) &&
                string.Equals(workspaceSnapshotKey, nextSnapshotKey, StringComparison.Ordinal))
            {
                return;
            }

            workspaceSnapshotKey = nextSnapshotKey;
            CurrentProjectId = workspace.CurrentProjectId;
            myProjects = workspace.MyProjects.ToList();
            ownedProjects = workspace.OwnedProjects.ToList();
            sharedProjects = workspace.SharedProjects.ToList();
            projectRoles = new Dictionary<string, string>(workspace.ProjectRoles, StringComparer.OrdinalIgnoreCase);
            currentUserRole = workspace.CurrentUserRole;
            Tasks = workspace.Tasks.ToList();
            chain = workspace.Chain.ToList();
            Commits = workspace.Commits.ToList();
            Artifacts = workspace.Artifacts.ToList();
            auditTrail = workspace.AuditTrail.ToList();
            projectMembers = workspace.ProjectMembers.ToList();
            UpdateImplementedProposalIds();

            if (workspace.CurrentProjectChanged)
            {
                await JoinSignalRGroups();
            }

            if (activeTab == ActiveTab.Governance)
            {
                await RefreshGovernance();
            }

            if (activeTab == ActiveTab.Documents)
            {
                await RefreshDocuments(preserveEditorDraft: isDocumentDraftDirty);
            }

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Sync Error: {ex.Message}");
        }
        finally
        {
            isSyncing = false;
        }
    }

    private string ResolveNodeUrlFromQuery(string fallback)
    {
        if (!bool.TryParse(Config["AllowNodeUrlQueryOverride"], out var allowOverride) || !allowOverride)
        {
            return fallback;
        }

        try
        {
            var uri = new Uri(Navigation.Uri);
            string query = uri.Query.TrimStart('?');
            foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), "nodeUrl", StringComparison.OrdinalIgnoreCase))
                {
                    string value = Uri.UnescapeDataString(parts[1]);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.TrimEnd('/');
                    }
                }
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static string BuildWorkspaceSnapshotKey(ProjectWorkspaceData workspace)
    {
        var lastBlock = workspace.Chain
            .OrderByDescending(block => block.Index)
            .FirstOrDefault();

        string projects = string.Join('|', workspace.MyProjects.OrderBy(project => project, StringComparer.OrdinalIgnoreCase));
        string ownedProjects = string.Join('|', workspace.OwnedProjects.OrderBy(project => project, StringComparer.OrdinalIgnoreCase));
        string sharedProjects = string.Join('|', workspace.SharedProjects.OrderBy(project => project, StringComparer.OrdinalIgnoreCase));
        string roles = string.Join('|', workspace.ProjectRoles
            .OrderBy(role => role.Key, StringComparer.OrdinalIgnoreCase)
            .Select(role => $"{role.Key}:{role.Value}"));
        string members = string.Join('|', workspace.ProjectMembers.OrderBy(member => member, StringComparer.OrdinalIgnoreCase));

        return string.Join("::",
            workspace.CurrentProjectId,
            workspace.CurrentUserRole,
            projects,
            ownedProjects,
            sharedProjects,
            roles,
            members,
            workspace.Tasks.Count,
            workspace.Chain.Count,
            lastBlock?.Index.ToString() ?? "",
            lastBlock?.Hash ?? "",
            workspace.Commits.Count,
            workspace.Artifacts.Count,
            workspace.AuditTrail.Count);
    }

    private static bool IsSameBlock(BlockModel left, BlockModel right)
    {
        return left.Index == right.Index &&
               string.Equals(left.Hash, right.Hash, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.ChannelId, right.ChannelId, StringComparison.OrdinalIgnoreCase);
    }

    private void StartWorkspaceRefreshLoop()
    {
        if (workspaceRefreshCts != null || !MyKeyService.IsLoggedIn)
        {
            return;
        }

        workspaceRefreshCts = new CancellationTokenSource();
        _ = RefreshWorkspacePeriodically(workspaceRefreshCts.Token);
    }

    private void StopWorkspaceRefreshLoop()
    {
        workspaceRefreshCts?.Cancel();
        workspaceRefreshCts?.Dispose();
        workspaceRefreshCts = null;
    }

    private async Task RefreshWorkspacePeriodically(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(WorkspaceRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (MyKeyService.IsLoggedIn)
                {
                    await InvokeAsync(RefreshPeers);
                    await InvokeAsync(SyncChain);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopWorkspaceRefreshLoop();
        await RealtimeClient.DisposeAsync();
    }

    private async Task ConnectGitRepository(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            statusMessage = "Enter a repository name or URL.";
            return;
        }

        if (!MyKeyService.IsLoggedIn || string.IsNullOrWhiteSpace(MyKeyService.PublicKey))
        {
            statusMessage = "Sign in before connecting a repository.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentProjectId) || CurrentProjectId == "System")
        {
            statusMessage = "Select a project before connecting a repository.";
            return;
        }

        if (!CanManageMembers)
        {
            statusMessage = "Only project Owner or Manager can connect a repository.";
            return;
        }

        string normalizedRepository = NormalizeRepositoryInput(repository);
        string actor = MyKeyService.UserName ?? "";
        string timestamp = DateTime.UtcNow.ToString("O");
        string signable = $"GIT_CONNECT:{CurrentProjectId}:{normalizedRepository}:{actor}:{timestamp}";

        try
        {
            isGitRepositoryConnecting = true;
            var result = await GitIntegrationClient.ConnectRepositoryAsync(new GitRepositoryConnectCommand(
                currentNodeUrl,
                CurrentProjectId,
                normalizedRepository,
                actor,
                MyKeyService.PublicKey ?? "",
                MyKeyService.SignData(signable),
                timestamp));

            statusMessage = result.Message;
            if (result.Success)
            {
                await LoadGitIntegrationStatus();
            }
        }
        catch (Exception ex)
        {
            statusMessage = $"Repository connect failed: {ex.Message}";
        }
        finally
        {
            isGitRepositoryConnecting = false;
            StateHasChanged();
        }
    }

    private static string NormalizeRepositoryInput(string repository)
    {
        string value = repository.Trim();
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            string host = uri.Host.Trim().ToLowerInvariant();
            string path = uri.AbsolutePath.Trim('/');
            return string.IsNullOrWhiteSpace(path) ? host : $"{host}/{path}";
        }

        return value;
    }

    private static string NormalizeTaskAssignee(string? assignee) =>
        string.IsNullOrWhiteSpace(assignee) ||
        string.Equals(assignee.Trim(), "None", StringComparison.OrdinalIgnoreCase)
            ? ""
            : assignee.Trim();

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

            string type = root.TryGetProperty("Type", out var t1) ? t1.GetString() ?? "" :
                (root.TryGetProperty("type", out var t2) ? t2.GetString() ?? "" : "");
            if (string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            string actor = root.TryGetProperty("User", out var u1) ? u1.GetString() ?? "" :
                (root.TryGetProperty("user", out var u2) ? u2.GetString() ?? "" : "");
            string projectId = root.TryGetProperty("ProjectId", out var p1) ? p1.GetString() ?? "" :
                (root.TryGetProperty("projectId", out var p2) ? p2.GetString() ?? "" : "");

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

    private static bool TryParseArtifactFromJson(JsonElement root, out ArtifactPayloadUI artifact)
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
            FileName = root.TryGetProperty("FileName", out var fn) ? fn.GetString() ?? "artifact.bin" : "artifact.bin",
            FileHash = root.TryGetProperty("FileHash", out var fh) ? fh.GetString() ?? "" : "",
            RegisteredBy = root.TryGetProperty("RegisteredBy", out var rb)
                ? rb.GetString() ?? ""
                : (root.TryGetProperty("User", out var u) ? u.GetString() ?? "" : ""),
            VerificationMethod = root.TryGetProperty("VerificationMethod", out var vm) ? vm.GetString() ?? "IPFS" : "IPFS",
            SizeBytes = root.TryGetProperty("SizeBytes", out var sizeProp) && sizeProp.TryGetInt64(out var size) ? size : 0,
            ContentType = root.TryGetProperty("ContentType", out var contentTypeProp) ? contentTypeProp.GetString() ?? "" : "",
            Timestamp = root.TryGetProperty("Timestamp", out var timestampProp) ? timestampProp.GetString() ?? "" : ""
        };

        return !string.IsNullOrWhiteSpace(artifact.FileHash);
    }

    private async Task CreateNewProject()
    {
        if (string.IsNullOrWhiteSpace(newProjectName)) return;
        if (!MyKeyService.IsLoggedIn)
        {
            statusMessage = "Sign in before creating a project.";
            return;
        }

        UserIdentityCheckResult? identity = null;
        try
        {
            identity = await BlockAnchoringService.CheckCurrentUserIdentityAsync();
        }
        catch (Exception ex) when (ShouldQueueBrowserOperation(ex.Message))
        {
            identity = null;
        }

        if (identity != null && !identity.Success)
        {
            statusMessage = $"Identity check failed: {identity.Message}";
            return;
        }

        if (identity != null && identity.Exists && !identity.PublicKeyMatches)
        {
            statusMessage = $"User name '{MyKeyService.UserName}' is already bound to another wallet. Restore the original wallet or choose a different user name.";
            return;
        }

        string cleanName = new string(newProjectName.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(cleanName)) return;

        string rawToHash = $"{MyKeyService.PublicKey}{DateTime.UtcNow.Ticks}{cleanName}";

        string channelHash = ComputeSimpleHash(rawToHash).Substring(0, 8);

        string channelId = $"{cleanName}_{channelHash}";

        string payloadJson = new JsonObject
        {
            ["Type"] = "CreateProject",
            ["User"] = MyKeyService.UserName,
            ["ProjectId"] = channelId,
            ["Timestamp"] = DateTime.UtcNow.ToString("O")
        }.ToJsonString();

        bool isSuccess = await SendJsonBlockJson(payloadJson, "System");

        if (isSuccess)
        {
            CurrentProjectId = channelId;
            newProjectName = "";
            await JoinSignalRGroups();
            await SyncChain();
        }
        else
        {
            Console.WriteLine("[UI] Project creation failed. Switch aborted.");
        }
    }

    private async Task DownloadAndFinishRegistration()
    {
        try
        {
            if (string.IsNullOrEmpty(newUserPassword))
            {
                statusMessage = "Please enter a password for encryption.";
                return;
            }

            statusMessage = "Generating keystore file...";
            passkeyModalMessage = "";
            StateHasChanged();

            string keystoreJson = MyKeyService.ExportKeystore(newUserPassword);
            await MyKeyService.StoreSessionKeystoreAsync(keystoreJson);

            await JS.InvokeVoidAsync("downloadTextFile", "nexus_wallet.json", keystoreJson);

            showPasswordEntry = false;
            statusMessage = "wallet.json successfully downloaded.";
            passkeyModalMessage = "";
        }
        catch (Exception ex)
        {
            statusMessage = $"Export failed: {ex.Message}";
            passkeyModalMessage = ex.Message;
        }
    }

    private async Task DownloadWithPasskeyAndFinishRegistration()
    {
        try
        {
            statusMessage = "Creating passkey-protected wallet.json...";
            passkeyModalMessage = "";
            StateHasChanged();

            string keystoreJson = await MyKeyService.ExportKeystoreWithPasskeyAsync();
            await MyKeyService.StoreSessionKeystoreAsync(keystoreJson);

            await JS.InvokeVoidAsync("downloadTextFile", "nexus_wallet.json", keystoreJson);

            showPasswordEntry = false;
            statusMessage = "Passkey-protected wallet.json downloaded.";
            passkeyModalMessage = "";
        }
        catch (Exception ex)
        {
            statusMessage = $"Passkey export failed: {ex.Message}";
            passkeyModalMessage = ex.Message;
        }
    }

    private async Task HandleTaskSigningAndMining(TaskSigningRequest args)
    {
        try
        {
            if (!CanCreateTask && args.Event.Type == "Create")
            {
                statusMessage = "A project is required before creating a work item.";
                return;
            }

            bool requiresAssignee =
                args.Event.Status == (int)ProjectTaskStatus.InProgress ||
                args.Event.Status == (int)ProjectTaskStatus.Done;
            string normalizedAssignee = NormalizeTaskAssignee(args.Event.Assignee);
            if (requiresAssignee && string.IsNullOrWhiteSpace(normalizedAssignee))
            {
                statusMessage = "Assign a project member before moving a work item to In Progress or Done.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(normalizedAssignee) &&
                !projectMembers.Contains(normalizedAssignee, StringComparer.OrdinalIgnoreCase))
            {
                statusMessage = $"Cannot assign '{normalizedAssignee}': user is not a member of this project.";
                return;
            }

            args.Event.Assignee = normalizedAssignee;

            isMining = true;
            statusMessage = "Finalizing block (PoC)...";
            StateHasChanged();

            string payloadJson = BuildTaskEventJson(args.Event);
            string signerPublicKey = MyKeyService.GetPublicKeyFromKeystore(args.Keystore);
            var result = await BlockAnchoringService.AnchorJsonStringWithKeystoreAsync(
                payloadJson,
                args.Event.ProjectId,
                args.Keystore,
                args.Password,
                signerPublicKey);
            statusMessage = result.Message;

            if (result.Success)
            {
                if (string.Equals(args.Event.Type, "Create", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(args.Event.ParentTaskId) &&
                    args.Event.ParentTaskId.StartsWith("GOV-", StringComparison.OrdinalIgnoreCase))
                {
                    locallyImplementedProposalIds.Add(args.Event.ParentTaskId);
                    implementedProposalIds.Add(args.Event.ParentTaskId);
                }

                await SyncChain();
            }
        }
        catch (Exception ex)
        {
            statusMessage = "Error: " + ex.Message;
        }
        finally
        {
            isMining = false;
            StateHasChanged();
        }
    }

    private static string BuildTaskEventJson(TaskEvent evt)
    {
        return new JsonObject
        {
            ["Type"] = evt.Type,
            ["TaskId"] = evt.TaskId,
            ["Title"] = evt.Title,
            ["Status"] = evt.Status,
            ["User"] = evt.User,
            ["ProjectId"] = evt.ProjectId,
            ["Assignee"] = evt.Assignee,
            ["Description"] = evt.Description,
            ["ParentTaskId"] = evt.ParentTaskId,
            ["BranchInfo"] = evt.BranchInfo,
            ["TargetUser"] = evt.TargetUser,
            ["TargetPublicKey"] = evt.TargetPublicKey,
            ["Role"] = evt.Role,
            ["Amount"] = evt.Amount,
            ["ProposalId"] = evt.ProposalId,
            ["Vote"] = evt.Vote
        }.ToJsonString();
    }

    private async Task HandleFileUpload(ArtifactUploadRequest file)
    {
        if (!MyKeyService.IsLoggedIn || !CanCreateTask) return;

        if (file.SizeBytes > 50 * 1024 * 1024)
        {
            statusMessage = "Error: File size exceeds the 50MB limit";
            return;
        }

        if (string.IsNullOrWhiteSpace(file.Base64Content))
        {
            statusMessage = "Error: File content is empty";
            return;
        }

        isUploading = true;
        StateHasChanged();

        try
        {
            statusMessage = "Uploading file to local IPFS node...";
            StateHasChanged();

            byte[] fileBytes = Convert.FromBase64String(file.Base64Content);
            string cid = await Ipfs.UploadFileAsync(fileBytes, file.FileName, file.ContentType);

            statusMessage = "Anchoring file CID to blockchain...";
            StateHasChanged();
            bool anchored = await AnchorArtifactViaNodeAsync(file, cid);
            if (!anchored)
            {
                return;
            }

            statusMessage = $"File successfully saved! CID: {cid.Substring(0, 8)}...";
        }
        catch (Exception ex)
        {
            statusMessage = $"Upload failed: {ex.Message}";
        }
        finally
        {
            isUploading = false;
            StateHasChanged();
        }
    }

    private async Task<bool> AnchorArtifactViaNodeAsync(ArtifactUploadRequest file, string cid)
    {
        var result = await ArtifactIntegrationClient.AnchorAsync(new ArtifactAnchorCommand(
            currentNodeUrl,
            CurrentProjectId,
            cid,
            file.FileName,
            file.SizeBytes,
            file.ContentType));

        if (result.Success)
        {
            await SyncChain();
            return true;
        }

        statusMessage = result.Message;
        return false;
    }

}




