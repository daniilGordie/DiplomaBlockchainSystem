using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Blockchain.Core;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Blockchain.Application.Analytics;
using Blockchain.Application.Projects;
using Blockchain.Application.Security;
using Blockchain.Application.Blocks;
using Blockchain.Core.Contracts;
using Blockchain.Node.Hubs;

namespace Blockchain.Node.Services
{
    public class BlockchainGrpcService : BlockchainService.BlockchainServiceBase
    {
        private readonly ILogger<BlockchainGrpcService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IChainReader _chainReader;
        private readonly ISmartContractStateReader _smartContractState;
        private readonly IProjectMembershipStore _projectMembershipStore;
        private readonly IPeerStore _peerStore;
        private readonly P2POptions _p2pOptions;
        private readonly BlockchainManager _blockchainManager;
        private readonly P2PNetworkService _p2pService;
        private readonly PeerChainSyncService _peerChainSync;
        private readonly ProjectResponseCache _projectResponseCache;
        private readonly GrpcBlockProcessor _blockProcessor;
        private readonly AuthorizeReadRequestUseCase _authorizeReadRequest;
        private readonly GetProjectAnalyticsUseCase _getProjectAnalytics;
        private readonly GetSecurityAuditUseCase _getSecurityAudit;
        private readonly GetProjectTasksUseCase _getProjectTasks;
        private readonly GetTaskHistoryUseCase _getTaskHistory;
        private readonly GetGovernanceProposalsUseCase _getGovernanceProposals;
        private readonly GetProjectDocumentsUseCase _getProjectDocuments;
        private readonly GetDocumentVersionsUseCase _getDocumentVersions;
        private readonly ProjectAccessPolicy _projectAccessPolicy;
        private readonly ProjectReadAccessGuard _projectReadAccess;
        private readonly ChainReadAccessGuard _chainReadAccess;
        private readonly UserReadAccessGuard _userReadAccess;

        private static readonly ConcurrentDictionary<string, byte> _syncedCommits = new();
        private static readonly ConcurrentDictionary<string, byte> _syncedArtifacts = new();
        public BlockchainGrpcService(
            ILogger<BlockchainGrpcService> logger,
            IConfiguration configuration,
            IChainReader chainReader,
            ISmartContractStateReader smartContractState,
            IProjectMembershipStore projectMembershipStore,
            IPeerStore peerStore,
            IOptions<P2POptions> p2pOptions,
            BlockchainManager manager,
            P2PNetworkService p2pService,
            PeerChainSyncService peerChainSync,
            ProjectResponseCache projectResponseCache,
            GrpcBlockProcessor blockProcessor,
            AuthorizeReadRequestUseCase authorizeReadRequest,
            GetProjectAnalyticsUseCase getProjectAnalytics,
            GetSecurityAuditUseCase getSecurityAudit,
            GetProjectTasksUseCase getProjectTasks,
            GetTaskHistoryUseCase getTaskHistory,
            GetGovernanceProposalsUseCase getGovernanceProposals,
            GetProjectDocumentsUseCase getProjectDocuments,
            GetDocumentVersionsUseCase getDocumentVersions,
            ProjectAccessPolicy projectAccessPolicy,
            ProjectReadAccessGuard projectReadAccess,
            ChainReadAccessGuard chainReadAccess,
            UserReadAccessGuard userReadAccess)
        {
            _logger = logger;
            _configuration = configuration;
            _chainReader = chainReader;
            _smartContractState = smartContractState;
            _projectMembershipStore = projectMembershipStore;
            _peerStore = peerStore;
            _p2pOptions = p2pOptions.Value;
            _blockchainManager = manager;
            _p2pService = p2pService;
            _peerChainSync = peerChainSync;
            _projectResponseCache = projectResponseCache;
            _blockProcessor = blockProcessor;
            _authorizeReadRequest = authorizeReadRequest;
            _getProjectAnalytics = getProjectAnalytics;
            _getSecurityAudit = getSecurityAudit;
            _getProjectTasks = getProjectTasks;
            _getTaskHistory = getTaskHistory;
            _getGovernanceProposals = getGovernanceProposals;
            _getProjectDocuments = getProjectDocuments;
            _getDocumentVersions = getDocumentVersions;
            _projectAccessPolicy = projectAccessPolicy;
            _projectReadAccess = projectReadAccess;
            _chainReadAccess = chainReadAccess;
            _userReadAccess = userReadAccess;

        }

        private bool IsAuthorizedReadRequest(
            ISmartContractStateReader smartContractState,
            string scope,
            string userName,
            string userPublicKey,
            string authSignature,
            string authTimestamp,
            string authNonce)
        {
            var result = _authorizeReadRequest.Execute(new AuthorizeReadRequestCommand(
                scope,
                userName,
                userPublicKey,
                authSignature,
                authTimestamp,
                authNonce));

            if (!result.Authorized)
            {
                _logger.LogWarning(
                    "[Security] Rejected read request for scope {Scope}: {Reason}.",
                    scope,
                    result.Message);
            }

            return result.Authorized;
        }

        private bool CanReadProjectScope(
            string projectId,
            string scope,
            string userName,
            string userPublicKey,
            string authSignature,
            string authTimestamp,
            string authNonce)
        {
            var result = _projectReadAccess.CanRead(new ProjectReadAccessRequest(
                projectId,
                scope,
                userName,
                userPublicKey,
                authSignature,
                authTimestamp,
                authNonce));

            if (!result.Allowed)
            {
                _logger.LogWarning(
                    "[Security] Rejected project read request for scope {Scope}: {Reason}.",
                    scope,
                    result.Reason);
                return false;
            }

            return true;
        }

        private bool IsValidAdminToken(string suppliedToken)
        {
            string configuredToken = _configuration["NodeAdminToken"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredToken) || string.IsNullOrWhiteSpace(suppliedToken))
            {
                return false;
            }

            byte[] left = System.Text.Encoding.UTF8.GetBytes(configuredToken);
            byte[] right = System.Text.Encoding.UTF8.GetBytes(suppliedToken);
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }

        public override async Task<StatusReply> AddPeer(PeerRequest request, ServerCallContext context)
        {
            if (!IsValidAdminToken(request.AdminToken))
            {
                _logger.LogWarning("[Security] Rejected AddPeer request: invalid admin token.");
                return new StatusReply { Success = false, Message = "Invalid admin token" };
            }

            if (IsSelfPeer(request.Url))
            {
                return new StatusReply { Success = false, Message = "Cannot add this node as its own peer" };
            }

            if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uriResult) &&
               (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
            {
                _p2pService.AddPeer(request.Url);
                _peerStore.SavePeer(request.Url);
                var sync = await _peerChainSync.SyncFromPeerAsync(request.Url);
                foreach (var channelId in sync.ChangedChannels)
                {
                    _projectResponseCache.InvalidateProject(channelId);
                }
                return new StatusReply { Success = true, Message = $"Peer added. Synced {sync.AcceptedBlocks} blocks." };
            }
            return new StatusReply { Success = false, Message = "Invalid URL format" };
        }

        public override Task<PeerListResponse> GetPeers(EmptyRequest request, ServerCallContext context)
        {
            var resp = new PeerListResponse();
            resp.Urls.AddRange(_p2pService.GetPeers());
            return Task.FromResult(resp);
        }

        public override Task<StatusReply> RegisterPeer(RegisterPeerRequest request, ServerCallContext context)
        {
            if (!IsValidRegistrationToken(request.RegistrationToken))
            {
                _logger.LogWarning("[Security] Rejected RegisterPeer request: invalid registration token.");
                return Task.FromResult(new StatusReply { Success = false, Message = "Invalid registration token" });
            }

            string publicUrl = P2POptions.NormalizeUrl(request.PublicUrl);
            if (!IsValidPeerUrl(publicUrl, allowLocalhost: IsLocalNetworkMode()))
            {
                return Task.FromResult(new StatusReply { Success = false, Message = "Invalid public URL" });
            }

            if (IsSelfPeer(publicUrl))
            {
                return Task.FromResult(new StatusReply { Success = false, Message = "Cannot register this node as its own peer" });
            }

            string role = string.IsNullOrWhiteSpace(request.Role) ? P2PNodeRole.Full.ToString() : request.Role.Trim();
            var peer = new PeerInfo(publicUrl, request.NodeId?.Trim() ?? string.Empty, role);
            _peerStore.SavePeer(peer);
            _p2pService.AddPeer(publicUrl);

            _logger.LogInformation("[P2P] Registered peer {NodeId} ({Role}) at {PublicUrl}.", peer.NodeId, peer.Role, peer.Url);
            return Task.FromResult(new StatusReply { Success = true, Message = "Peer registered" });
        }

        public override Task<PeerDirectoryResponse> GetPeerDirectory(EmptyRequest request, ServerCallContext context)
        {
            var response = new PeerDirectoryResponse
            {
                CurrentNodeId = _p2pOptions.EffectiveNodeId,
                CurrentPublicUrl = _p2pOptions.NormalizedPublicUrl,
                CurrentRole = _p2pOptions.Role.ToString()
            };
            foreach (var peer in _peerStore.LoadPeerInfos())
            {
                response.Peers.Add(new PeerDirectoryItem
                {
                    NodeId = peer.NodeId ?? string.Empty,
                    PublicUrl = peer.Url,
                    Role = string.IsNullOrWhiteSpace(peer.Role) ? P2PNodeRole.Full.ToString() : peer.Role,
                    LastSeen = peer.LastSeen ?? string.Empty,
                    LastFailure = peer.LastFailure ?? string.Empty,
                    IsTrusted = peer.IsTrusted
                });
            }

            return Task.FromResult(response);
        }

        public override Task<KnownChannelsResponse> GetKnownChannels(EmptyRequest request, ServerCallContext context)
        {
            var response = new KnownChannelsResponse();
            response.ChannelIds.AddRange(_chainReader.GetKnownChannels());
            return Task.FromResult(response);
        }

        public override async Task<StatusReply> BroadcastBlock(BlockModel request, ServerCallContext context)
        {
            var result = await _blockProcessor.ProcessBroadcastAsync(request);
            if (result.Success)
            {
                TrackPayloadForAnalytics(request.Data);
                _projectResponseCache.InvalidateProject(result.ChannelId);
            }

            return new StatusReply { Success = result.Success, Message = result.Message };
        }

        public override async Task<StatusReply> ReceiveBlock(BlockModel request, ServerCallContext context)
        {
            var result = await _blockProcessor.ProcessReceivedAsync(request);
            if (result.Success)
            {
                TrackPayloadForAnalytics(request.Data);
                _projectResponseCache.InvalidateProject(result.ChannelId);
            }

            return new StatusReply { Success = result.Success, Message = result.Message };
        }

        public override Task<BlockModel> GetLastBlock(EmptyRequest request, ServerCallContext context)
        {
            var b = _blockchainManager.GetLatestBlock();
            if (b == null)
            {
                return Task.FromResult(new BlockModel { Index = 0, Data = "", Hash = "0", PreviousHash = "0", Timestamp = DateTime.UtcNow.ToString("O"), ValidatorPublicKey = "", Signature = "", Nonce = 0 });
            }
            return Task.FromResult(GrpcProjectMapper.ToBlockModel(b));
        }

        public override Task<ChainResponse> GetChain(ChainRequest request, ServerCallContext context)
        {
            var response = new ChainResponse();

            string channelToRead = string.IsNullOrEmpty(request.ChannelId) ? "System" : request.ChannelId;
            string userName = string.IsNullOrEmpty(request.UserName) ? "Guest" : request.UserName;

            bool isNodeSync = IsValidNodeSyncRequest(userName, request.SyncToken);
            if (!isNodeSync)
            {
                var access = _chainReadAccess.CanRead(new ChainReadAccessRequest(
                    channelToRead,
                    $"CHAIN:{channelToRead}",
                    userName,
                    request.UserPublicKey,
                    request.AuthSignature,
                    request.AuthTimestamp,
                    request.AuthNonce));

                if (!access.Allowed)
                {
                    _logger.LogWarning(
                        "[Security] Rejected chain read request for channel {ChannelId}: {Reason}.",
                        channelToRead,
                        access.Reason);
                    return Task.FromResult(response);
                }
            }

            int requestedCount = request.Count > 0 ? request.Count : 100;
            int count = Math.Clamp(requestedCount, 1, 500);
            var blocks = request.AfterIndex >= 0
                ? _chainReader.LoadChain(channelToRead)
                    .Where(block => block.Index > request.AfterIndex)
                    .OrderBy(block => block.Index)
                    .Take(count)
                    .ToList()
                : _chainReader.LoadLatestBlocks(channelToRead, count)
                    .OrderBy(block => block.Index)
                    .ToList();

            foreach (var block in blocks)
            {
                response.Blocks.Add(GrpcProjectMapper.ToBlockModel(block));
            }

            return Task.FromResult(response);
        }

        private bool IsValidNodeSyncRequest(string userName, string syncToken)
        {
            if (!string.Equals(userName, P2PNetworkService.NodeSyncUser, StringComparison.Ordinal))
            {
                return false;
            }

            string configuredToken = _p2pOptions.SyncToken;
            if (string.IsNullOrWhiteSpace(configuredToken) || string.IsNullOrWhiteSpace(syncToken))
            {
                return false;
            }

            byte[] left = System.Text.Encoding.UTF8.GetBytes(configuredToken);
            byte[] right = System.Text.Encoding.UTF8.GetBytes(syncToken);
            return CryptographicOperations.FixedTimeEquals(left, right);
        }

        private bool IsValidRegistrationToken(string suppliedToken)
        {
            string configuredToken = _p2pOptions.RegistrationToken;
            if (string.IsNullOrWhiteSpace(configuredToken) || string.IsNullOrWhiteSpace(suppliedToken))
            {
                return false;
            }

            byte[] left = System.Text.Encoding.UTF8.GetBytes(configuredToken);
            byte[] right = System.Text.Encoding.UTF8.GetBytes(suppliedToken);
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }

        public override Task<TaskResponse> GetProjectTasks(ProjectRequest request, ServerCallContext context)
        {
            var response = new TaskResponse();

            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!CanReadProjectScope(projectId, $"PROJECT:{projectId}:TASKS", userName, request.UserPublicKey, request.AuthSignature, request.AuthTimestamp, request.AuthNonce))
            {
                _logger.LogWarning($"[Security] User '{userName}' attempted to access tasks for project '{projectId}' without permissions.");
                return Task.FromResult(response);
            }

            var result = _getProjectTasks.Execute(projectId, userName);
            response.UserRole = result.UserRole;
            foreach (var task in result.Tasks)
            {
                response.Tasks.Add(GrpcProjectMapper.ToTaskItem(task));
            }

            return Task.FromResult(response);
        }

        public override Task<TaskHistoryResponse> GetTaskHistory(TaskHistoryRequest request, ServerCallContext context)
        {
            var response = new TaskHistoryResponse();

            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!CanReadProjectScope(projectId, $"PROJECT:{projectId}:TASK_HISTORY:{request.TaskId}", userName, request.UserPublicKey, request.AuthSignature, request.AuthTimestamp, request.AuthNonce))
            {
                return Task.FromResult(response);
            }

            foreach (var item in _getTaskHistory.Execute(projectId, request.TaskId))
            {
                response.Items.Add(GrpcProjectMapper.ToTaskHistoryItem(item));
            }
            return Task.FromResult(response);
        }

        public override Task<GovernanceResponse> GetGovernanceProposals(ProjectRequest request, ServerCallContext context)



        {
            var response = new GovernanceResponse();

            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!CanReadProjectScope(projectId, $"PROJECT:{projectId}:GOVERNANCE", userName, request.UserPublicKey, request.AuthSignature, request.AuthTimestamp, request.AuthNonce))
            {
                return Task.FromResult(response);
            }

            foreach (var proposal in _getGovernanceProposals.Execute(projectId, userName))
            {
                response.Proposals.Add(GrpcProjectMapper.ToGovernanceProposalItem(proposal));
            }

            return Task.FromResult(response);
        }

        public override Task<AnalyticsResponse> GetAnalytics(ProjectRequest request, ServerCallContext context)
        {
            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!CanReadProjectScope(projectId, $"PROJECT:{projectId}:ANALYTICS", userName, request.UserPublicKey, request.AuthSignature, request.AuthTimestamp, request.AuthNonce))
            {
                return Task.FromResult(new AnalyticsResponse());
            }

            if (_projectResponseCache.TryGetAnalytics(projectId, out var cachedAnalytics))
            {
                return Task.FromResult(cachedAnalytics);
            }

            var resp = GrpcAnalyticsMapper.ToAnalyticsResponse(_getProjectAnalytics.Execute(projectId));

            _projectResponseCache.SetAnalytics(projectId, resp);
            return Task.FromResult(resp);
        }

        public override Task<DocumentResponse> GetProjectDocuments(ProjectRequest request, ServerCallContext context)
        {
            var response = new DocumentResponse();
            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!CanReadProjectScope(projectId, $"PROJECT:{projectId}:DOCUMENTS", userName, request.UserPublicKey, request.AuthSignature, request.AuthTimestamp, request.AuthNonce))
            {
                return Task.FromResult(response);
            }

            foreach (var document in _getProjectDocuments.Execute(projectId))
            {
                response.Documents.Add(GrpcProjectMapper.ToDocumentSummary(document));
            }

            return Task.FromResult(response);
        }

        public override Task<DocumentVersionResponse> GetDocumentVersions(DocumentHistoryRequest request, ServerCallContext context)
        {
            var response = new DocumentVersionResponse();
            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!CanReadProjectScope(projectId, $"PROJECT:{projectId}:DOCUMENT:{request.DocumentId}", userName, request.UserPublicKey, request.AuthSignature, request.AuthTimestamp, request.AuthNonce))
            {
                return Task.FromResult(response);
            }

            foreach (var version in _getDocumentVersions.Execute(projectId, request.DocumentId))
            {
                response.Versions.Add(GrpcProjectMapper.ToDocumentVersionItem(version));
            }

            return Task.FromResult(response);
        }

        public override Task<SecurityAuditResponse> GetSecurityAudit(ProjectRequest request, ServerCallContext context)
        {
            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!CanReadProjectScope(projectId, $"PROJECT:{projectId}:SECURITY_AUDIT", userName, request.UserPublicKey, request.AuthSignature, request.AuthTimestamp, request.AuthNonce))
            {
                return Task.FromResult(new SecurityAuditResponse
                {
                    ChainValid = false,
                    CheckedBlocks = 0
                });
            }

            if (_projectResponseCache.TryGetSecurityAudit(projectId, out var cachedAudit))
            {
                return Task.FromResult(cachedAudit);
            }

            var response = GrpcAnalyticsMapper.ToSecurityAuditResponse(_getSecurityAudit.Execute(projectId));

            _projectResponseCache.SetSecurityAudit(projectId, response);
            return Task.FromResult(response);
        }

        public override Task<ProjectListResponse> GetMyProjects(UserRequest request, ServerCallContext context)
        {
            var response = new ProjectListResponse();

            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;
            var access = _userReadAccess.CanRead(new UserReadAccessRequest(
                $"USER:{userName}:PROJECTS",
                userName,
                request.UserPublicKey,
                request.AuthSignature,
                request.AuthTimestamp,
                request.AuthNonce));

            if (!access.Allowed)
            {
                _logger.LogWarning(
                    "[Security] Rejected user read request for {UserName}: {Reason}.",
                    userName,
                    access.Reason);
                return Task.FromResult(response);
            }

            var projects = _projectMembershipStore.GetUserProjects(userName);

            response.ProjectIds.AddRange(projects);

            return Task.FromResult(response);
        }

        public override Task<UserIdentityResponse> GetUserIdentity(UserRequest request, ServerCallContext context)
        {
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;
            var access = _authorizeReadRequest.Execute(new AuthorizeReadRequestCommand(
                $"USER:{userName}:IDENTITY",
                userName,
                request.UserPublicKey,
                request.AuthSignature,
                request.AuthTimestamp,
                request.AuthNonce));

            string? boundPublicKey = _smartContractState.GetUserPublicKey(userName);
            bool exists = !string.IsNullOrWhiteSpace(boundPublicKey);
            bool matches = !exists || string.Equals(boundPublicKey, request.UserPublicKey, StringComparison.Ordinal);

            if (access.Status == AuthorizeReadRequestStatus.PublicKeyMismatch)
            {
                return Task.FromResult(new UserIdentityResponse
                {
                    Success = true,
                    Exists = true,
                    PublicKeyMatches = false,
                    Message = "User name is already bound to another wallet key."
                });
            }

            if (!access.Authorized)
            {
                return Task.FromResult(new UserIdentityResponse
                {
                    Success = false,
                    Exists = exists,
                    PublicKeyMatches = matches,
                    Message = access.Message
                });
            }

            return Task.FromResult(new UserIdentityResponse
            {
                Success = true,
                Exists = exists,
                PublicKeyMatches = matches,
                Message = exists ? "User identity is bound to this wallet key." : "User name is available."
            });
        }

        public override Task<UserNameAvailabilityResponse> CheckUserName(UserNameRequest request, ServerCallContext context)
        {
            string userName = request.UserName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(userName) ||
                string.Equals(userName, "Guest", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new UserNameAvailabilityResponse
                {
                    Success = false,
                    Exists = false,
                    Message = "Enter a non-empty user name."
                });
            }

            bool exists = !string.IsNullOrWhiteSpace(_smartContractState.GetUserPublicKey(userName));
            return Task.FromResult(new UserNameAvailabilityResponse
            {
                Success = true,
                Exists = exists,
                Message = exists
                    ? "User name is already bound to a wallet."
                    : "User name is available."
            });
        }

        private static void TrackPayloadForAnalytics(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";

                if (type == "CodeCommit" && root.TryGetProperty("CommitHash", out var commitHashProp))
                {
                    string commitHash = commitHashProp.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(commitHash))
                    {
                        _syncedCommits.TryAdd(commitHash.ToLowerInvariant(), 1);
                    }
                }

                if (root.TryGetProperty("FileHash", out var fileHashProp))
                {
                    string fileHash = fileHashProp.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(fileHash))
                    {
                        _syncedArtifacts.TryAdd(fileHash, 1);
                    }
                }
            }
            catch
            {
                // Non-JSON payloads are ignored by analytics tracking.
            }
        }

        private bool IsSelfPeer(string peerUrl)
        {
            string publicUrl = _p2pOptions.NormalizedPublicUrl;
            return !string.IsNullOrWhiteSpace(publicUrl)
                && !string.IsNullOrWhiteSpace(peerUrl)
                && string.Equals(publicUrl, P2POptions.NormalizeUrl(peerUrl), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsValidPeerUrl(string peerUrl, bool allowLocalhost)
        {
            if (!Uri.TryCreate(peerUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            return allowLocalhost
                || (!uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    && uri.Host != "127.0.0.1"
                    && uri.Host != "::1");
        }

        private bool IsLocalNetworkMode()
        {
            return _p2pOptions.NormalizedBootstrapPeers.Any(IsLoopbackUrl)
                || IsLoopbackUrl(_p2pOptions.NormalizedPublicUrl);
        }

        private static bool IsLoopbackUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;
        }
    }
}
