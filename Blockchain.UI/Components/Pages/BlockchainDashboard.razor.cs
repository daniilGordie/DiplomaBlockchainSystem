using Blockchain.Node;
using Blockchain.UI.Services;
using Blockchain.UI.Components;
using Blockchain.UI.Components.Dashboard;
using Blockchain.UI.Models;
using Google.Protobuf;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Blockchain.UI.Components.Pages;

public partial class BlockchainDashboard : ComponentBase
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private KeyService MyKeyService { get; set; } = default!;
    [Inject] private IpfsService Ipfs { get; set; } = default!;
    [Inject] private IConfiguration Config { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;

    private BlockchainService.BlockchainServiceClient BlockchainClient { get; set; } = default!;
    private HubConnection? hubConnection;
    private string currentNodeUrl = "";
    private string nodeAdminToken = "";
    private string nodeUrlInput = "";
    private string peerUrlInput = "";
    private string nodeConnectionStatus = "";
    private bool isPeerPanelBusy = false;
    private List<string> peerUrls = new();

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

    private string CurrentProjectId = "System";
    private string userNameInput = "";
    private string restoreMnemonic = "";
    private string statusMessage = "";
    private bool isRestoreMode = false;
    private bool isUploading = false;
    private bool isMining = false;
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
    private Task OnNewMemberNameChanged(string value) { newMemberName = value; return Task.CompletedTask; }
    private Task OnNewMemberRoleChanged(string value) { newMemberRole = value; return Task.CompletedTask; }
    private Task OnNewMemberPublicKeyChanged(string value) { newMemberPublicKey = value; return Task.CompletedTask; }
    private Task OnNewMemberPublicKeyFingerprintChanged(string value) { newMemberPublicKeyFingerprint = value; return Task.CompletedTask; }
    private Task OnNewProposalTitleChanged(string value) { newProposalTitle = value; return Task.CompletedTask; }
    private Task OnNewProposalDescriptionChanged(string value) { newProposalDescription = value; return Task.CompletedTask; }
    private Task OnDocumentTitleChanged(string value) { documentTitle = value; return Task.CompletedTask; }
    private Task OnDocumentContentChanged(string value) { documentContent = value; return Task.CompletedTask; }
    private Task OnNodeUrlInputChanged(string value) { nodeUrlInput = value; return Task.CompletedTask; }
    private Task OnPeerUrlInputChanged(string value) { peerUrlInput = value; return Task.CompletedTask; }
    private Task CancelRestoreMode() { isRestoreMode = false; return Task.CompletedTask; }
    private string MnemonicCopyButtonText => mnemonicCopyButtonText;


    protected override async Task OnInitializedAsync()
    {
        isGenesisModeActive = bool.TryParse(Config["GenesisModeEnabled"], out var genesis) && genesis;

        currentNodeUrl = NormalizeNodeUrl(Config["NodeUrl"] ?? "https://localhost:7066");
        nodeAdminToken = Config["NodeAdminToken"] ?? "";
        nodeUrlInput = currentNodeUrl;
        BlockchainClient = CreateBlockchainClient(currentNodeUrl);

        try
        {
            await StartRealtimeConnection();
            await RefreshPeers();
            await LoadIntegrationStatus();
        }
        catch (Exception ex)
        {
            nodeConnectionStatus = $"Node unavailable: {ex.Message}";
        }
    }

    private static string NormalizeNodeUrl(string url) => url.Trim().TrimEnd('/');

    private static bool IsHttpNodeUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static BlockchainService.BlockchainServiceClient CreateBlockchainClient(string nodeUrl)
    {
        var httpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler());
        var channel = GrpcChannel.ForAddress(nodeUrl, new GrpcChannelOptions
        {
            HttpHandler = httpHandler
        });

        return new BlockchainService.BlockchainServiceClient(channel);
    }

    private string BuildReadAuthSignature(string scope)
    {
        if (!MyKeyService.IsLoggedIn || string.IsNullOrWhiteSpace(MyKeyService.PublicKey))
        {
            return string.Empty;
        }

        string userName = MyKeyService.UserName ?? "Guest";
        string publicKey = MyKeyService.PublicKey ?? string.Empty;
        string signable = $"READ:{scope}:{userName}:{publicKey}";
        return MyKeyService.SignData(signable);
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

    private async Task CopyMyPublicKey()
    {
        if (string.IsNullOrWhiteSpace(MyKeyService.PublicKey))
        {
            statusMessage = "No public key available.";
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", MyKeyService.PublicKey);
            statusMessage = "Public key copied to clipboard.";
        }
        catch (Exception ex)
        {
            statusMessage = $"Clipboard error: {ex.Message}";
        }
    }

    private async Task StartRealtimeConnection()
    {
        if (hubConnection != null)
        {
            await hubConnection.DisposeAsync();
        }

        hubConnection = new HubConnectionBuilder()
            .WithUrl($"{currentNodeUrl}/blockchainHub")
            .WithAutomaticReconnect()
            .Build();

        hubConnection.On<BlockModel>("NewBlockBroadcast", async (block) =>
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

        await hubConnection.StartAsync();
        nodeConnectionStatus = $"Connected to {currentNodeUrl}.";

        if (MyKeyService.IsLoggedIn)
        {
            await JoinSignalRGroups();
        }
    }

    private async Task ConnectToNode()
    {
        string nextUrl = NormalizeNodeUrl(nodeUrlInput);
        if (!IsHttpNodeUrl(nextUrl))
        {
            nodeConnectionStatus = "Enter a valid HTTP or HTTPS node URL.";
            return;
        }

        try
        {
            isPeerPanelBusy = true;
            currentNodeUrl = nextUrl;
            nodeUrlInput = currentNodeUrl;
            BlockchainClient = CreateBlockchainClient(currentNodeUrl);

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

    private async Task AddPeer()
    {
        if (string.IsNullOrWhiteSpace(nodeAdminToken))
        {
            statusMessage = "Node admin token is not configured in UI settings.";
            return;
        }

        string peerUrl = NormalizeNodeUrl(peerUrlInput);
        if (!IsHttpNodeUrl(peerUrl))
        {
            nodeConnectionStatus = "Enter a valid HTTP or HTTPS peer URL.";
            return;
        }

        try
        {
            isPeerPanelBusy = true;
            var response = await BlockchainClient.AddPeerAsync(new PeerRequest { Url = peerUrl, AdminToken = nodeAdminToken });
            statusMessage = response.Success ? $"Peer added: {peerUrl}." : $"Peer rejected: {response.Message}";

            if (response.Success)
            {
                peerUrlInput = "";
                await RefreshPeers();
            }
        }
        catch (Exception ex)
        {
            nodeConnectionStatus = $"Add peer failed: {ex.Message}";
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
        if (BlockchainClient == null)
        {
            return;
        }

        try
        {
            isPeerPanelBusy = true;
            var response = await BlockchainClient.GetPeersAsync(new EmptyRequest());
            peerUrls = response.Urls.ToList();
            nodeConnectionStatus = $"Connected to {currentNodeUrl}. {peerUrls.Count} peer(s) registered.";
        }
        catch (Exception ex)
        {
            peerUrls.Clear();
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
        try
        {
            using var doc = await Http.GetFromJsonAsync<JsonDocument>($"{currentNodeUrl}/api/integrations/git/status");
            var root = doc?.RootElement;
            string webhookUrl = root?.TryGetProperty("webhookUrl", out var webhookProp) == true ? webhookProp.GetString() ?? "" : "";
            string mode = root?.TryGetProperty("mode", out var modeProp) == true ? modeProp.GetString() ?? "" : "";
            string source = root?.TryGetProperty("source", out var sourceProp) == true ? sourceProp.GetString() ?? "" : "";
            string defaultProjectId = root?.TryGetProperty("defaultProjectId", out var defaultProjectProp) == true ? defaultProjectProp.GetString() ?? "" : "";
            string bindingPolicy = root?.TryGetProperty("bindingPolicy", out var bindingPolicyProp) == true ? bindingPolicyProp.GetString() ?? "" : "";
            int bindingsCount = root?.TryGetProperty("bindingsCount", out var bindingsCountProp) == true ? bindingsCountProp.GetInt32() : 0;
            bool secretConfigured = root?.TryGetProperty("webhookSecretConfigured", out var secretProp) == true && secretProp.GetBoolean();
            var providers = new List<string>();
            var bindings = new List<GitRepositoryBindingUI>();
            if (root?.TryGetProperty("providers", out var providersProp) == true && providersProp.ValueKind == JsonValueKind.Array)
            {
                providers = providersProp.EnumerateArray()
                    .Select(x => x.GetString() ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            }
            if (root?.TryGetProperty("bindings", out var bindingsProp) == true && bindingsProp.ValueKind == JsonValueKind.Array)
            {
                bindings = bindingsProp.EnumerateArray()
                    .Select(x => new GitRepositoryBindingUI
                    {
                        Repository = x.TryGetProperty("repository", out var repoProp) ? repoProp.GetString() ?? "" : "",
                        ProjectId = x.TryGetProperty("projectId", out var projectProp) ? projectProp.GetString() ?? "" : ""
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Repository) || !string.IsNullOrWhiteSpace(x.ProjectId))
                    .ToList();
            }

            string details = mode;
            if (!string.IsNullOrWhiteSpace(source))
            {
                details += $" | source: {source}";
            }
            if (providers.Any())
            {
                details += $" | providers: {string.Join(", ", providers)}";
            }
            if (!string.IsNullOrWhiteSpace(bindingPolicy))
            {
                details += $" | {bindingPolicy}";
            }
            if (!string.IsNullOrWhiteSpace(defaultProjectId))
            {
                details += $" | default project: {defaultProjectId}";
            }
            details += $" | bindings: {bindingsCount}";

            gitIntegrationStatus = new IntegrationStatusUI
            {
                IsLoaded = true,
                IsHealthy = secretConfigured,
                Name = "Git webhook",
                Status = secretConfigured ? "Ready" : "Secret missing",
                Endpoint = webhookUrl,
                Details = details,
                Source = source,
                Providers = providers,
                RepositoryBindings = bindings
            };
        }
        catch (Exception ex)
        {
            gitIntegrationStatus = new IntegrationStatusUI
            {
                IsLoaded = true,
                IsHealthy = false,
                Name = "Git webhook",
                Status = "Unavailable",
                Endpoint = $"{currentNodeUrl}/api/webhooks/git",
                Details = ex.Message
            };
        }
    }

    private async Task LoadIpfsIntegrationStatus()
    {
        try
        {
            using var doc = await Http.GetFromJsonAsync<JsonDocument>($"{currentNodeUrl}/api/integrations/ipfs/health");
            var root = doc?.RootElement;
            string apiUrl = root?.TryGetProperty("apiUrl", out var apiProp) == true ? apiProp.GetString() ?? "" : "";
            bool reachable = root?.TryGetProperty("reachable", out var reachableProp) == true && reachableProp.GetBoolean();

            ipfsIntegrationStatus = new IntegrationStatusUI
            {
                IsLoaded = true,
                IsHealthy = reachable,
                Name = "IPFS node",
                Status = reachable ? "Reachable" : "Offline",
                Endpoint = apiUrl,
                Details = reachable ? "Local IPFS API responded" : "Start the local IPFS daemon"
            };
        }
        catch (Exception ex)
        {
            ipfsIntegrationStatus = new IntegrationStatusUI
            {
                IsLoaded = true,
                IsHealthy = false,
                Name = "IPFS node",
                Status = "Unavailable",
                Endpoint = "http://127.0.0.1:5001/api/v0",
                Details = ex.Message
            };
        }
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
            }
            else
            {
                StateHasChanged();
            }
        }
    }

    private async Task JoinSignalRGroups()
    {
        if (hubConnection != null && hubConnection.State == HubConnectionState.Connected && MyKeyService.IsLoggedIn)
        {
            try
            {
                // CHANGED: secure realtime registration (signature bound to SignalR connection id).
                string registerChallenge = $"REGISTER:{hubConnection.ConnectionId}:{MyKeyService.UserName}";
                string registerSignature = MyKeyService.SignData(registerChallenge);
                bool registerOk = await hubConnection.InvokeAsync<bool>("RegisterUser", MyKeyService.UserName, MyKeyService.PublicKey, registerSignature);
                if (!registerOk)
                {
                    statusMessage = "Realtime auth failed. User group subscription denied.";
                    return;
                }
                await hubConnection.InvokeAsync("JoinProject", "System");
                if (!string.IsNullOrEmpty(CurrentProjectId))
                {
                    await hubConnection.InvokeAsync("JoinProject", CurrentProjectId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SignalR] Error subscribing to channel: {ex.Message}");
            }
        }
    }

    private string ComputeSimpleHash(string data)
    {
        using var sha256 = SHA256.Create();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data);
        return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private async Task MineBlockLocal(BlockModel block)
    {
        await Task.Delay(10);

        DateTime dt = DateTime.Parse(block.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind);
        string timeStringS = dt.ToString("s");

        block.Nonce = 0;

        using var sha256 = SHA256.Create();

        string baseData = $"{block.Index}{timeStringS}{block.Data}{block.PreviousHash}{block.ValidatorPublicKey}{block.Signature}";

        while (true)
        {
            block.Nonce++;
            string rawData = baseData + block.Nonce;

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(rawData);
            byte[] hashBytes = sha256.ComputeHash(bytes);

            // Fast Hex conversion
            block.Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            if (block.Hash.StartsWith("000"))
                break;

            if (block.Nonce % 500 == 0)
            {
                await Task.Yield();
            }
        }
    }

    private (bool RefreshMembership, bool RefreshGovernance) ApplyBlockToUI(BlockModel block)
    {
        if (string.IsNullOrWhiteSpace(block.Data)) return (false, false);
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
                            Status = root.TryGetProperty("Status", out var sProp) ? (ProjectTaskStatus)sProp.GetInt32() : ProjectTaskStatus.Todo
                        });
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
            if (TryBuildAuditTrailEntry(block, out var entry))
            {
                auditTrail.Insert(0, entry);
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

        var payload = new
        {
            Type = "AssignRole",
            User = MyKeyService.UserName,
            ProjectId = CurrentProjectId,
            TargetUser = newMemberName,
            TargetPublicKey = newMemberPublicKey,
            Role = newMemberRole,
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        await SendJsonBlock(payload);
        newMemberName = "";
        newMemberPublicKey = "";
        newMemberPublicKeyFingerprint = "";
    }

    private string FormatJson(string json) { try { using var doc = JsonDocument.Parse(json); return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }); } catch { return json; } }

    private async Task CreateWallet()
    {
        if (!string.IsNullOrWhiteSpace(userNameInput))
        {
            await Logout();
            await MyKeyService.CreateNewWallet(userNameInput);
            showPasswordEntry = true;
            await JoinSignalRGroups();
            await SyncChain();
        }
    }

    private void RestoreMode() => isRestoreMode = !isRestoreMode;

    private async Task RestoreWallet()
    {
        if (await MyKeyService.RestoreWallet(restoreMnemonic, userNameInput))
        {
            isRestoreMode = false;
            await JoinSignalRGroups();
            await SyncChain();
        }
    }

    private async Task CopySecretPhraseToClipboard()
    {
        if (string.IsNullOrWhiteSpace(MyKeyService.CurrentMnemonic))
        {
            statusMessage = "Secret phrase is unavailable.";
            return;
        }

        bool copied = await TryCopyTextToClipboardAsync(MyKeyService.CurrentMnemonic);
        if (copied)
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
            statusMessage = "Copy failed. Please copy the secret phrase manually.";
        }
    }
    private async Task Logout() => await MyKeyService.Logout();

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
    }

    private Task CreateTaskFromProposal(string proposalId)
    {
        if (!CanCreateTask || !IsCurrentUserOwner || string.IsNullOrWhiteSpace(proposalId))
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
            var response = await BlockchainClient.GetTaskHistoryAsync(new TaskHistoryRequest
            {
                TaskId = taskId,
                ProjectId = CurrentProjectId,
                UserName = MyKeyService.UserName ?? "Guest",
                UserPublicKey = MyKeyService.PublicKey ?? "",
                AuthSignature = BuildReadAuthSignature($"PROJECT:{CurrentProjectId}:TASK_HISTORY:{taskId}")
            });
            viewingHistory = response.Items.OrderByDescending(x => x.Timestamp).ToList();
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
            var response = await BlockchainClient.GetGovernanceProposalsAsync(new ProjectRequest
            {
                ProjectId = CurrentProjectId,
                UserName = MyKeyService.UserName ?? "Guest",
                UserPublicKey = MyKeyService.PublicKey ?? "",
                AuthSignature = BuildReadAuthSignature($"PROJECT:{CurrentProjectId}:GOVERNANCE")
            });
            governanceProposals = response.Proposals.ToList();
        }
        catch (Exception ex)
        {
            statusMessage = "Governance Error: " + ex.Message;
        }
    }

    private async Task CreateProposal()
    {
        if (!CanCreateTask || string.IsNullOrWhiteSpace(newProposalTitle)) return;

        var payload = new
        {
            Type = "CreateProposal",
            ProposalId = "GOV-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper(),
            Title = newProposalTitle.Trim(),
            Description = newProposalDescription.Trim(),
            User = MyKeyService.UserName,
            ProjectId = CurrentProjectId,
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        bool ok = await SendJsonBlock(payload);
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

        var payload = new
        {
            Type = "CastVote",
            ProposalId = args.ProposalId,
            Vote = args.VoteYes,
            User = MyKeyService.UserName,
            ProjectId = CurrentProjectId,
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        bool ok = await SendJsonBlock(payload);
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
        if (!CanCreateTask)
        {
            documents = new List<DocumentSummary>();
            documentVersions = new List<DocumentVersionItem>();
            selectedDocumentId = "";
            documentTitle = "";
            documentContent = "";
            return;
        }

        try
        {
            var response = await BlockchainClient.GetProjectDocumentsAsync(new ProjectRequest
            {
                ProjectId = CurrentProjectId,
                UserName = MyKeyService.UserName ?? "Guest",
                UserPublicKey = MyKeyService.PublicKey ?? "",
                AuthSignature = BuildReadAuthSignature($"PROJECT:{CurrentProjectId}:DOCUMENTS")
            });

            documents = response.Documents.ToList();

            if (!string.IsNullOrWhiteSpace(selectedDocumentId))
            {
                var selected = documents.FirstOrDefault(d => d.DocumentId == selectedDocumentId);
                if (selected == null)
                {
                    selectedDocumentId = "";
                    documentVersions = new List<DocumentVersionItem>();
                }
                else
                {
                    documentTitle = selected.Title;
                    documentContent = selected.Content;
                    await LoadDocumentVersions(selectedDocumentId);
                }
            }
        }
        catch (Exception ex)
        {
            statusMessage = "Document Error: " + ex.Message;
        }
    }

    private async Task SelectDocument(string documentId)
    {
        selectedDocumentId = documentId;
        var selected = documents.FirstOrDefault(d => d.DocumentId == documentId);
        if (selected != null)
        {
            documentTitle = selected.Title;
            documentContent = selected.Content;
        }

        await LoadDocumentVersions(documentId);
    }

    private async Task LoadDocumentVersions(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId)) return;

        var response = await BlockchainClient.GetDocumentVersionsAsync(new DocumentHistoryRequest
        {
            ProjectId = CurrentProjectId,
            DocumentId = documentId,
            UserName = MyKeyService.UserName ?? "Guest",
            UserPublicKey = MyKeyService.PublicKey ?? "",
            AuthSignature = BuildReadAuthSignature($"PROJECT:{CurrentProjectId}:DOCUMENT:{documentId}")
        });

        documentVersions = response.Versions.ToList();
    }

    private async Task SaveDocument()
    {
        if (!CanCreateTask || string.IsNullOrWhiteSpace(documentTitle)) return;

        bool isNewDocument = string.IsNullOrWhiteSpace(selectedDocumentId);
        string documentId = isNewDocument
            ? "DOC-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant()
            : selectedDocumentId;

        var payload = new
        {
            Type = isNewDocument ? "CreateDocument" : "UpdateDocument",
            DocumentId = documentId,
            Title = documentTitle.Trim(),
            Content = documentContent ?? "",
            User = MyKeyService.UserName,
            ProjectId = CurrentProjectId,
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        bool ok = await SendJsonBlock(payload);
        if (ok)
        {
            selectedDocumentId = documentId;
            await RefreshDocuments();
            statusMessage = isNewDocument ? "Document created and anchored." : "New document version anchored.";
        }
    }

    private async Task LoadAnalytics()
    {
        activeTab = ActiveTab.Analytics;
        try
        {
            analyticsData = await BlockchainClient.GetAnalyticsAsync(new ProjectRequest
            {
                ProjectId = CurrentProjectId,
                UserName = MyKeyService.UserName ?? "Guest",
                UserPublicKey = MyKeyService.PublicKey ?? "",
                AuthSignature = BuildReadAuthSignature($"PROJECT:{CurrentProjectId}:ANALYTICS")
            });

            securityAudit = await BlockchainClient.GetSecurityAuditAsync(new ProjectRequest
            {
                ProjectId = CurrentProjectId,
                UserName = MyKeyService.UserName ?? "Guest",
                UserPublicKey = MyKeyService.PublicKey ?? "",
                AuthSignature = BuildReadAuthSignature($"PROJECT:{CurrentProjectId}:SECURITY_AUDIT")
            });
        }
        catch (Exception ex)
        {
            statusMessage = ex.Message;
        }
    }

    private async Task<bool> SendJsonBlock<T>(T payload)
    {
        try
        {
            isMining = true;
            StateHasChanged();

            string json = JsonSerializer.Serialize(payload);
            string targetChannel = (json.Contains("\"Type\":\"CreateProject\"") || json.Contains("\"Type\":\"AssignRole\""))
                                    ? "System"
                                    : CurrentProjectId;

            var cR = await BlockchainClient.GetChainAsync(new ChainRequest
                {
                    Count = 1,
                    ChannelId = targetChannel,
                    UserName = MyKeyService.UserName ?? "Guest",
                    UserPublicKey = MyKeyService.PublicKey ?? "",
                    AuthSignature = BuildReadAuthSignature($"CHAIN:{targetChannel}")
                });

            string prevHash = cR.Blocks.Count > 0 ? cR.Blocks.Last().Hash : "0";
            int expectedIndex = cR.Blocks.Count > 0 ? cR.Blocks.Last().Index + 1 : 0;
            string timestamp = DateTime.UtcNow.ToString("O");
            string pubKey = MyKeyService.PublicKey ?? "";

            string signableData = $"{expectedIndex}{timestamp}{json}{prevHash}";
            string sig = MyKeyService.SignData(signableData);

            var blk = new BlockModel
                {
                    Index = expectedIndex,
                    Data = json,
                    Timestamp = timestamp,
                    PreviousHash = prevHash,
                    Hash = ComputeSimpleHash(signableData),
                    ValidatorPublicKey = pubKey,
                    Signature = sig,
                    ChannelId = targetChannel
                };

            await MineBlockLocal(blk);

            var r = await BlockchainClient.ReceiveBlockAsync(blk);
            statusMessage = r.Success ? "Success. Block anchored via PoC." : $"Rejected: {r.Message}";

            if (r.Success) await SyncChain();

            return r.Success;
        }
        catch (Exception ex)
        {
            statusMessage = ex.Message;
            return false;
        }
        finally { isMining = false; StateHasChanged(); }
    }

    private async Task SyncChain()
    {
        try
        {
            if (MyKeyService.IsLoggedIn)
            {
                var projRes = await BlockchainClient.GetMyProjectsAsync(new UserRequest
                {
                    UserName = MyKeyService.UserName,
                    UserPublicKey = MyKeyService.PublicKey ?? "",
                    AuthSignature = BuildReadAuthSignature($"USER:{MyKeyService.UserName}:PROJECTS")
                });
                myProjects = projRes.ProjectIds.ToList();
                await RefreshProjectRoleBucketsAsync();

                if ((!myProjects.Contains(CurrentProjectId) || CurrentProjectId == "System") && myProjects.Count > 0)
                {
                    CurrentProjectId = myProjects.First();
                    await JoinSignalRGroups();
                }
            }

            var tR = await BlockchainClient.GetProjectTasksAsync(new ProjectRequest
                {
                    ProjectId = CurrentProjectId,
                    UserName = MyKeyService.UserName ?? "Guest",
                    UserPublicKey = MyKeyService.PublicKey ?? "",
                    AuthSignature = BuildReadAuthSignature($"PROJECT:{CurrentProjectId}:TASKS")
                });

            currentUserRole = string.IsNullOrWhiteSpace(tR.UserRole) ? "None" : tR.UserRole;
            Tasks = tR.Tasks.Select(t => new ProjectTask { Id = t.Id, Title = t.Title, Creator = t.Creator, Assignee = t.Assignee, Status = (ProjectTaskStatus)t.Status, ProjectId = t.ProjectId, Description = t.Description, ParentTaskId = t.ParentTaskId, BranchInfo = t.BranchInfo }).ToList();
            UpdateImplementedProposalIds();
            await RefreshProjectMembersAsync();

            if (Tasks.Count > 0 || MyKeyService.IsLoggedIn)
            {
                var cR = await BlockchainClient.GetChainAsync(new ChainRequest
                    {
                        Count = 50,
                        ChannelId = CurrentProjectId,
                        UserName = MyKeyService.UserName ?? "Guest",
                        UserPublicKey = MyKeyService.PublicKey ?? "",
                        AuthSignature = BuildReadAuthSignature($"CHAIN:{CurrentProjectId}")
                    });
                chain = cR.Blocks.ToList();
                RebuildLocalArtifactsAndCommits(chain);
                RebuildAuditTrail(chain);
            }
            else
            {
                chain = new List<BlockModel>();
                Commits.Clear();
                Artifacts.Clear();
                auditTrail.Clear();
                projectMembers.Clear();
            }

            if (activeTab == ActiveTab.Governance)
            {
                await RefreshGovernance();
            }

            if (activeTab == ActiveTab.Documents)
            {
                await RefreshDocuments();
            }

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Sync Error: {ex.Message}");
        }
    }

    private async Task RefreshProjectRoleBucketsAsync()
    {
        var nextRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectId in myProjects.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string role = "None";
            try
            {
                var response = await BlockchainClient.GetProjectTasksAsync(new ProjectRequest
                {
                    ProjectId = projectId,
                    UserName = MyKeyService.UserName ?? "Guest",
                    UserPublicKey = MyKeyService.PublicKey ?? "",
                    AuthSignature = BuildReadAuthSignature($"PROJECT:{projectId}:TASKS")
                });

                role = string.IsNullOrWhiteSpace(response.UserRole) ? "None" : response.UserRole;
            }
            catch
            {
                role = "None";
            }

            nextRoles[projectId] = role;
        }

        projectRoles = nextRoles;
        ownedProjects = myProjects
            .Where(projectId => projectRoles.TryGetValue(projectId, out var role) &&
                                string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            .OrderBy(projectId => projectId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        sharedProjects = myProjects
            .Where(projectId => !projectRoles.TryGetValue(projectId, out var role) ||
                                !string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            .OrderBy(projectId => projectId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projectRoles.TryGetValue(CurrentProjectId, out var currentRole))
        {
            currentUserRole = currentRole;
        }
        else if (!myProjects.Contains(CurrentProjectId, StringComparer.OrdinalIgnoreCase))
        {
            currentUserRole = "None";
        }
    }

    private async Task RefreshProjectMembersAsync()
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(MyKeyService.UserName) && currentUserRole != "None")
        {
            members.Add(MyKeyService.UserName);
        }

        try
        {
            var systemChain = await BlockchainClient.GetChainAsync(new ChainRequest
            {
                Count = 500,
                ChannelId = "System",
                UserName = MyKeyService.UserName ?? "Guest",
                UserPublicKey = MyKeyService.PublicKey ?? "",
                AuthSignature = BuildReadAuthSignature("CHAIN:System")
            });

            foreach (var block in systemChain.Blocks)
            {
                AddProjectMemberFromBlock(block, members);
            }
        }
        catch
        {
            // Fallback below still preserves current user and known task participants.
        }

        foreach (var task in Tasks)
        {
            AddKnownMember(task.Creator, members);
            AddKnownMember(task.Assignee, members);
        }

        projectMembers = members
            .Where(member => !string.Equals(member, "None", StringComparison.OrdinalIgnoreCase))
            .OrderBy(member => member, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddProjectMemberFromBlock(BlockModel block, HashSet<string> members)
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
            if (!string.Equals(projectId, CurrentProjectId, StringComparison.OrdinalIgnoreCase))
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

    private void RebuildLocalArtifactsAndCommits(List<BlockModel> blocks)
    {
        var cL = new List<CommitPayloadUI>();
        var aL = new List<ArtifactPayloadUI>();

        foreach (var b in blocks)
        {
            if (string.IsNullOrWhiteSpace(b.Data)) continue;

            try
            {
                using var doc = JsonDocument.Parse(b.Data);
                var root = doc.RootElement;

                string eventType = "";
                if (root.TryGetProperty("Type", out var t1)) eventType = t1.GetString() ?? "";
                else if (root.TryGetProperty("type", out var t2)) eventType = t2.GetString() ?? "";

                if (eventType == "CodeCommit")
                {
                    cL.Add(new CommitPayloadUI
                        {
                            Repository = root.TryGetProperty("Repository", out var r) || root.TryGetProperty("repository", out r) ? r.GetString() ?? "Unknown" : "Unknown",
                            CommitHash = root.TryGetProperty("CommitHash", out var h) || root.TryGetProperty("commitHash", out h) ? h.GetString() ?? "" : "",
                            Message = root.TryGetProperty("Message", out var m) || root.TryGetProperty("message", out m) ? m.GetString() ?? "" : "",
                            Author = root.TryGetProperty("User", out var u) || root.TryGetProperty("user", out u) ? u.GetString() ?? "Anon" : "Anon",
                            Provider = root.TryGetProperty("Provider", out var provider) ? provider.GetString() ?? "" : "",
                            Branch = root.TryGetProperty("Branch", out var branch) ? branch.GetString() ?? "" : ""
                        });
                }
                else if (TryParseArtifactFromJson(root, out var artifact))
                {
                    aL.Add(artifact);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] Failed to parse JSON in block {b.Hash}: {ex.Message}");
            }
        }

        Commits = cL.AsEnumerable().Reverse().ToList();
        Artifacts = aL;
    }

    private void RebuildAuditTrail(List<BlockModel> blocks)
    {
        var result = new List<AuditTrailEntry>();

        foreach (var block in blocks.OrderByDescending(x => x.Index))
        {
            if (TryBuildAuditTrailEntry(block, out var entry))
            {
                result.Add(entry);
            }
        }

        auditTrail = result;
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

        string cleanName = new string(newProjectName.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(cleanName)) return;

        string rawToHash = $"{MyKeyService.PublicKey}{DateTime.UtcNow.Ticks}{cleanName}";

        string channelHash = ComputeSimpleHash(rawToHash).Substring(0, 8);

        string channelId = $"{cleanName}_{channelHash}";

        var payload = new
        {
            Type = "CreateProject",
            User = MyKeyService.UserName,
            ProjectId = channelId,
            Timestamp = DateTime.UtcNow.ToString("O")
        };

        bool isSuccess = await SendJsonBlock(payload);

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

            // CHANGED: password-based wallet export path retained for compatibility.
            string keystoreJson = MyKeyService.ExportKeystore(newUserPassword);

            var fileStream = new MemoryStream(Encoding.UTF8.GetBytes(keystoreJson));
            using var streamRef = new DotNetStreamReference(stream: fileStream);

            await JS.InvokeVoidAsync("downloadFileFromStream", "nexus_wallet.json", streamRef);

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

    // CHANGED: native passkey-based wallet export path (no custom password required).
    private async Task DownloadWithPasskeyAndFinishRegistration()
    {
        try
        {
            statusMessage = "Creating passkey-protected wallet.json...";
            passkeyModalMessage = "";
            StateHasChanged();

            string keystoreJson = await MyKeyService.ExportKeystoreWithPasskeyAsync();

            var fileStream = new MemoryStream(Encoding.UTF8.GetBytes(keystoreJson));
            using var streamRef = new DotNetStreamReference(stream: fileStream);

            await JS.InvokeVoidAsync("downloadFileFromStream", "nexus_wallet.json", streamRef);

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

    private async Task HandleTaskSigningAndMining((TaskEvent Event, string Keystore, string Password) args)
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

            string json = JsonSerializer.Serialize(args.Event);
            string targetChannel = args.Event.ProjectId;

            var chainResp = await BlockchainClient.GetChainAsync(new ChainRequest
                {
                    Count = 1,
                    ChannelId = targetChannel,
                    UserName = MyKeyService.UserName,
                    UserPublicKey = MyKeyService.PublicKey ?? "",
                    AuthSignature = BuildReadAuthSignature($"CHAIN:{targetChannel}")
                });

            string prevHash = chainResp.Blocks.Count > 0 ? chainResp.Blocks.Last().Hash : "0";
            int expectedIndex = chainResp.Blocks.Count > 0 ? chainResp.Blocks.Last().Index + 1 : 0;
            string timestamp = DateTime.UtcNow.ToString("O");
            string pubKey = MyKeyService.PublicKey ?? "";

            string signableData = $"{expectedIndex}{timestamp}{json}{prevHash}";

            // CHANGED: supports both legacy password keystore and passkey-protected keystore.
            string correctSignature;
            if (args.Keystore.Contains("\"ProtectionMode\":\"passkey\"", StringComparison.OrdinalIgnoreCase))
            {
                correctSignature = await MyKeyService.SignDataWithPasskeyKeystoreAsync(args.Keystore, signableData);
            }
            else
            {
                correctSignature = MyKeyService.SignDataWithKeystore(args.Keystore, args.Password, signableData);
            }

            var blk = new BlockModel
                {
                    Index = expectedIndex,
                    Data = json,
                    Timestamp = timestamp,
                    PreviousHash = prevHash,
                    Hash = ComputeSimpleHash(signableData),
                    ValidatorPublicKey = pubKey,
                    Signature = correctSignature,
                    ChannelId = targetChannel
                };

            await MineBlockLocal(blk);

            var r = await BlockchainClient.ReceiveBlockAsync(blk);
            statusMessage = r.Success ? "Work item successfully written to the blockchain." : $"Node error: {r.Message}";

            if (r.Success) await SyncChain();
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

    private async Task HandleFileUpload(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file == null || !MyKeyService.IsLoggedIn || !CanCreateTask) return;

        if (file.Size > 50 * 1024 * 1024)
        {
            statusMessage = "Error: File size exceeds the 50MB limit";
            return;
        }

        isUploading = true;
        StateHasChanged();

        try
        {
            statusMessage = "Uploading file to local IPFS node...";
            StateHasChanged();

            string cid = await Ipfs.UploadFileAsync(file);

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

    private async Task<bool> AnchorArtifactViaNodeAsync(IBrowserFile file, string cid)
    {
        string actor = MyKeyService.UserName ?? "";
        string timestamp = DateTime.UtcNow.ToString("O");
        string signable = $"ARTIFACT_REGISTER:{CurrentProjectId}:{cid}:{actor}:{timestamp}";
        string signature = MyKeyService.SignData(signable);

        var request = new ArtifactAnchorRequest
        {
            ProjectId = CurrentProjectId,
            FileHash = cid,
            FileName = file.Name,
            SizeBytes = file.Size,
            ContentType = file.ContentType ?? "application/octet-stream",
            User = actor,
            RegisteredBy = actor,
            VerificationMethod = "IPFS CID",
            Timestamp = timestamp,
            UserPublicKey = MyKeyService.PublicKey ?? "",
            UserSignature = signature
        };

        using var response = await Http.PostAsJsonAsync($"{currentNodeUrl}/api/integrations/artifacts/register", request);
        if (response.IsSuccessStatusCode)
        {
            await SyncChain();
            return true;
        }

        string details = await response.Content.ReadAsStringAsync();
        statusMessage = $"Artifact anchor failed ({(int)response.StatusCode}): {details}";
        return false;
    }

    private async Task<bool> TryCopyTextToClipboardAsync(string text)
    {
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
            return true;
        }
        catch
        {
            try
            {
                return await JS.InvokeAsync<bool>("copyTextFallback", text);
            }
            catch
            {
                return false;
            }
        }
    }

    private sealed class ArtifactAnchorRequest
    {
        public string ProjectId { get; set; } = "";
        public string FileHash { get; set; } = "";
        public string FileName { get; set; } = "";
        public long SizeBytes { get; set; }
        public string ContentType { get; set; } = "";
        public string User { get; set; } = "";
        public string RegisteredBy { get; set; } = "";
        public string VerificationMethod { get; set; } = "IPFS CID";
        public string Timestamp { get; set; } = "";
        public string UserPublicKey { get; set; } = "";
        public string UserSignature { get; set; } = "";
    }
}




