using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Blockchain.Core;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Blockchain.Core.Contracts;
using Microsoft.AspNetCore.SignalR;
using System.Security.Cryptography;
using Blockchain.Node.Hubs;
using System.Collections.Concurrent;

namespace Blockchain.Node.Services
{
    public class BlockchainGrpcService : BlockchainService.BlockchainServiceBase
    {
        private readonly ILogger<BlockchainGrpcService> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _dbFileName;
        private readonly string _dbPassword;
        private readonly BlockchainManager _blockchainManager;
        private readonly OracleIdentity _oracleIdentity;
        private readonly P2PNetworkService _p2pService;
        private readonly IHubContext<BlockchainHub> _hubContext;

        private static readonly ConcurrentDictionary<string, byte> _syncedCommits = new();
        private static readonly ConcurrentDictionary<string, byte> _syncedArtifacts = new();

        private string DbConnectionString => $"Data Source={_dbFileName};Password={_dbPassword}";

        public BlockchainGrpcService(
            ILogger<BlockchainGrpcService> logger,
            IConfiguration configuration,
            BlockchainManager manager,
            OracleIdentity oracleIdentity,
            P2PNetworkService p2pService,
            IHubContext<BlockchainHub> hubContext)
        {
            _logger = logger;
            _configuration = configuration;
            _blockchainManager = manager;
            _oracleIdentity = oracleIdentity;
            _p2pService = p2pService;
            _hubContext = hubContext;

            _dbFileName = GetNodeDatabaseName(_configuration);

            _dbPassword = GetRequiredConfiguration(_configuration, "NodeDbPassword");
        }

        private static string GetRequiredConfiguration(IConfiguration configuration, string key)
        {
            string? value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"{key} is not configured. Set it via .NET user-secrets or environment variables.");
            }

            return value;
        }

        private static string GetNodeDatabaseName(IConfiguration configuration)
        {
            string port = configuration["Urls"]?.Split(':').LastOrDefault()?.Replace("/", "") ?? "5041";
            string? configuredDbName = configuration.GetConnectionString("DefaultNodeDb");

            if (string.IsNullOrWhiteSpace(configuredDbName) ||
                (configuredDbName == "nexus_node_5041.db" && port != "5041"))
            {
                return $"nexus_node_{port}.db";
            }

            return configuredDbName;
        }

        private bool VerifySignature(string data, string signatureBase64, string publicKeyBase64)
        {
            try
            {
                byte[] signatureBytes = Convert.FromBase64String(signatureBase64);

                if (signatureBytes.Length != 64)
                {
                    _logger.LogWarning($"[Security] Rejected: Invalid signature length ({signatureBytes.Length} bytes). Potential deserialization attack vector.");
                    return false;
                }

                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

                byte[] dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
                return ecdsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Security] Critical signature verification error: {ex.Message}");
                return false;
            }
        }

        private bool IsAuthorizedReadRequest(
            DatabaseManager db,
            string scope,
            string userName,
            string userPublicKey,
            string authSignature)
        {
            if (string.IsNullOrWhiteSpace(userName) ||
                string.Equals(userName, "Guest", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(userPublicKey) ||
                string.IsNullOrWhiteSpace(authSignature))
            {
                _logger.LogWarning("[Security] Rejected read request for scope {Scope}: missing signed identity.", scope);
                return false;
            }

            string signable = $"READ:{scope}:{userName}:{userPublicKey}";
            if (!VerifySignature(signable, authSignature, userPublicKey))
            {
                _logger.LogWarning("[Security] Rejected read request for scope {Scope}: invalid signature.", scope);
                return false;
            }

            string? boundPublicKey = db.GetUserPublicKey(userName);
            if (!string.IsNullOrWhiteSpace(boundPublicKey) &&
                !string.Equals(boundPublicKey, userPublicKey, StringComparison.Ordinal))
            {
                _logger.LogWarning("[Security] Rejected read request for user {UserName}: public key mismatch.", userName);
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
                var db = new DatabaseManager(_dbFileName, _dbPassword);
                db.SavePeer(request.Url);
                int syncedBlocks = await SyncFromPeerAsync(request.Url);
                return new StatusReply { Success = true, Message = $"Peer added. Synced {syncedBlocks} blocks." };
            }
            return new StatusReply { Success = false, Message = "Invalid URL format" };
        }

        public override Task<PeerListResponse> GetPeers(EmptyRequest request, ServerCallContext context)
        {
            var resp = new PeerListResponse();
            resp.Urls.AddRange(_p2pService.GetPeers());
            return Task.FromResult(resp);
        }

        public override Task<KnownChannelsResponse> GetKnownChannels(EmptyRequest request, ServerCallContext context)
        {
            var db = new DatabaseManager(_dbFileName, _dbPassword);
            var response = new KnownChannelsResponse();
            response.ChannelIds.AddRange(db.GetKnownChannels());
            return Task.FromResult(response);
        }

        public override async Task<StatusReply> BroadcastBlock(BlockModel request, ServerCallContext context)
        {
            var peerBlock = ToBlock(request);

            bool isAccepted = _blockchainManager.ProcessPeerBlock(peerBlock);

            if (isAccepted)
            {
                TrackPayloadForAnalytics(request.Data);
                await NotifyClientsAsync(request);
            }

            return new StatusReply { Success = isAccepted, Message = isAccepted ? "Accepted" : "Rejected" };
        }

        public override async Task<StatusReply> ReceiveBlock(BlockModel request, ServerCallContext context)
        {
            try
            {
                string signableData = $"{request.Index}{request.Timestamp}{request.Data}{request.PreviousHash}";
                bool isSigValid = VerifySignature(signableData, request.Signature, request.ValidatorPublicKey);

                if (!isSigValid)
                    return new StatusReply { Success = false, Message = "Crypto Fraud Detected: Invalid Signature" };

                var db = new DatabaseManager(_dbFileName, _dbPassword);

                var contract = new TaskContract();
                if (!contract.Validate(request.Data, request.ValidatorPublicKey, db))
                    return new StatusReply { Success = false, Message = "Access Denied by Smart Contract" };

                var newBlock = ToBlock(request);

                TrackPayloadForAnalytics(request.Data);

                bool isAccepted = _blockchainManager.ProcessPeerBlock(newBlock);
                if (!isAccepted)
                {
                    return new StatusReply { Success = false, Message = "Rejected by blockchain validation" };
                }

                await NotifyClientsAsync(request);
                await _p2pService.BroadcastBlockAsync(newBlock);

                return new StatusReply { Success = true, Message = "Block anchored successfully via PoC" };
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ReceiveBlock] Error: {ex.Message}");
                return new StatusReply { Success = false, Message = $"Server Error: {ex.Message}" };
            }
        }

        public override Task<BlockModel> GetLastBlock(EmptyRequest request, ServerCallContext context)
        {
            var b = _blockchainManager.GetLatestBlock();
            if (b == null)
            {
                return Task.FromResult(new BlockModel { Index = 0, Data = "", Hash = "0", PreviousHash = "0", Timestamp = DateTime.UtcNow.ToString("O"), ValidatorPublicKey = "", Signature = "", Nonce = 0 });
            }
            return Task.FromResult(new BlockModel { Index = b.Index, Data = b.Data, Hash = b.Hash, PreviousHash = b.PreviousHash, Timestamp = b.Timestamp.ToString("O"), ValidatorPublicKey = b.ValidatorPublicKey ?? "", Signature = b.Signature ?? "", Nonce = b.Nonce });
        }

        public override Task<ChainResponse> GetChain(ChainRequest request, ServerCallContext context)
        {
            var response = new ChainResponse();

            var db = new DatabaseManager(_dbFileName, _dbPassword);

            string channelToRead = string.IsNullOrEmpty(request.ChannelId) ? "System" : request.ChannelId;
            string userName = string.IsNullOrEmpty(request.UserName) ? "Guest" : request.UserName;

            bool isNodeSync = IsValidNodeSyncRequest(userName, request.SyncToken);
            if (!isNodeSync && !IsAuthorizedReadRequest(db, $"CHAIN:{channelToRead}", userName, request.UserPublicKey, request.AuthSignature))
            {
                return Task.FromResult(response);
            }

            string role = isNodeSync ? "NodeSync" : db.GetUserRole(channelToRead, userName);
            if (!isNodeSync && role == "None" && channelToRead != "System")
            {
                return Task.FromResult(response);
            }

            var blocks = db.LoadChain(channelToRead);

            foreach (var block in blocks.TakeLast(request.Count > 0 ? request.Count : 100))
            {
                response.Blocks.Add(new BlockModel
                {
                    Index = block.Index,
                    Timestamp = block.Timestamp.ToString("O"),
                    Data = block.Data,
                    PreviousHash = block.PreviousHash,
                    Hash = block.Hash,
                    ValidatorPublicKey = block.ValidatorPublicKey ?? "",
                    Signature = block.Signature ?? "",
                    Nonce = block.Nonce,
                    ChannelId = block.ChannelId
                });
            }

            return Task.FromResult(response);
        }

        private bool IsValidNodeSyncRequest(string userName, string syncToken)
        {
            if (!string.Equals(userName, P2PNetworkService.NodeSyncUser, StringComparison.Ordinal))
            {
                return false;
            }

            string configuredToken = _configuration["P2P:SyncToken"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(configuredToken) || string.IsNullOrWhiteSpace(syncToken))
            {
                return false;
            }

            byte[] left = System.Text.Encoding.UTF8.GetBytes(configuredToken);
            byte[] right = System.Text.Encoding.UTF8.GetBytes(syncToken);
            return CryptographicOperations.FixedTimeEquals(left, right);
        }

        public override Task<TaskResponse> GetProjectTasks(ProjectRequest request, ServerCallContext context)
        {
            var response = new TaskResponse();

            var db = new DatabaseManager(_dbFileName, _dbPassword);

            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!IsAuthorizedReadRequest(db, $"PROJECT:{projectId}:TASKS", userName, request.UserPublicKey, request.AuthSignature))
            {
                return Task.FromResult(response);
            }

            string role = db.GetUserRole(projectId, userName);
            response.UserRole = role;

            if (role == "None" && projectId != "System")
            {
                _logger.LogWarning($"[Security] User '{userName}' attempted to access tasks for project '{projectId}' without permissions.");
                return Task.FromResult(response);
            }

            try
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(DbConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = "SELECT TaskId, Title, Creator, Assignee, Status, ProjectId, Description, ParentTaskId, BranchInfo FROM Tasks WHERE ProjectId = $p";
                cmd.Parameters.AddWithValue("$p", projectId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    response.Tasks.Add(new TaskItem
                    {
                        Id = reader.GetString(0),
                        Title = db.DecryptStoredValue(reader.GetString(1)),
                        Creator = db.DecryptStoredValue(reader.GetString(2)),
                        Assignee = db.DecryptStoredValue(reader.GetString(3)),
                        Status = reader.GetInt32(4),
                        ProjectId = reader.GetString(5),
                        Description = reader.IsDBNull(6) ? "" : db.DecryptStoredValue(reader.GetString(6)),
                        ParentTaskId = reader.IsDBNull(7) ? "" : db.DecryptStoredValue(reader.GetString(7)),
                        BranchInfo = reader.IsDBNull(8) ? "" : db.DecryptStoredValue(reader.GetString(8))
                    });
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "DB Read Error in Tasks"); }

            return Task.FromResult(response);
        }

        public override Task<TaskHistoryResponse> GetTaskHistory(TaskHistoryRequest request, ServerCallContext context)
        {
            var response = new TaskHistoryResponse();

            var db = new DatabaseManager(_dbFileName, _dbPassword);
            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!IsAuthorizedReadRequest(db, $"PROJECT:{projectId}:TASK_HISTORY:{request.TaskId}", userName, request.UserPublicKey, request.AuthSignature))
            {
                return Task.FromResult(response);
            }

            string role = db.GetUserRole(projectId, userName);
            if (role == "None" && projectId != "System")
            {
                return Task.FromResult(response);
            }

            var blocks = db.LoadChain(projectId);

            foreach (var b in blocks)
            {
                if (!b.Data.Contains(request.TaskId)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(b.Data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("TaskId", out var tId) && tId.GetString() == request.TaskId)
                    {
                        var action = root.TryGetProperty("Type", out var type) ? type.GetString() : "Update";
                        var user = root.TryGetProperty("User", out var u) ? u.GetString() : "System";
                        var statusId = root.TryGetProperty("Status", out var s) ? s.GetInt32() : 0;
                        string statusLabel = statusId == 0 ? "To Do" : (statusId == 1 ? "In Progress" : "Done");

                        response.Items.Add(new TaskHistoryItem { Timestamp = b.Timestamp.ToString("g"), User = user, Action = action, StatusLabel = statusLabel, BlockIndex = b.Index });
                    }
                }
                catch { }
            }
            return Task.FromResult(response);
        }

        public override Task<GovernanceResponse> GetGovernanceProposals(ProjectRequest request, ServerCallContext context)



        {
            var response = new GovernanceResponse();

            var db = new DatabaseManager(_dbFileName, _dbPassword);
            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!IsAuthorizedReadRequest(db, $"PROJECT:{projectId}:GOVERNANCE", userName, request.UserPublicKey, request.AuthSignature))
            {
                return Task.FromResult(response);
            }

            string role = db.GetUserRole(projectId, userName);
            if (role == "None" && projectId != "System")
            {
                return Task.FromResult(response);
            }

            try
            {
                using var conn = new SqliteConnection(DbConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT p.ProposalId, p.ProjectId, p.Title, p.Description, p.CreatedBy, p.CreatedAt,
                           p.Status, p.YesVotes, p.NoVotes, v.Vote
                    FROM GovernanceProposals p
                    LEFT JOIN GovernanceVotes v ON v.ProposalId = p.ProposalId AND v.UserName = $user
                    WHERE p.ProjectId = $project
                    ORDER BY p.CreatedAt DESC";
                cmd.Parameters.AddWithValue("$project", projectId);
                cmd.Parameters.AddWithValue("$user", userName);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string userVote = "";
                    if (!reader.IsDBNull(9))
                    {
                        userVote = reader.GetInt32(9) == 1 ? "Yes" : "No";
                    }

                    response.Proposals.Add(new GovernanceProposalItem
                    {
                        ProposalId = reader.GetString(0),
                        ProjectId = reader.GetString(1),
                        Title = db.DecryptStoredValue(reader.GetString(2)),
                        Description = reader.IsDBNull(3) ? "" : db.DecryptStoredValue(reader.GetString(3)),
                        CreatedBy = db.DecryptStoredValue(reader.GetString(4)),
                        CreatedAt = reader.GetString(5),
                        Status = reader.GetString(6),
                        YesVotes = reader.GetInt32(7),
                        NoVotes = reader.GetInt32(8),
                        UserVote = userVote
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DB Read Error in Governance");
            }

            return Task.FromResult(response);
        }

        public override Task<AnalyticsResponse> GetAnalytics(ProjectRequest request, ServerCallContext context)
        {
            var db = new DatabaseManager(_dbFileName, _dbPassword);
            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!IsAuthorizedReadRequest(db, $"PROJECT:{projectId}:ANALYTICS", userName, request.UserPublicKey, request.AuthSignature))
            {
                return Task.FromResult(new AnalyticsResponse());
            }

            string role = db.GetUserRole(projectId, userName);
            if (role == "None" && projectId != "System")
            {
                return Task.FromResult(new AnalyticsResponse());
            }

            var blocks = db.LoadChain(projectId);

            var payloadCounts = CountProjectPayloads(blocks);
            var resp = new AnalyticsResponse
            {
                TotalBlocks = blocks.Count(),
                TotalCommits = payloadCounts.CommitCount,
                TotalArtifacts = payloadCounts.ArtifactCount
            };

            try
            {
                using var conn = new SqliteConnection(DbConnectionString);
                conn.Open();

                using var cmdT = conn.CreateCommand();
                cmdT.CommandText = "SELECT COUNT(1) FROM Tasks WHERE ProjectId = $p";
                cmdT.Parameters.AddWithValue("$p", projectId);
                resp.TotalTasks = Convert.ToInt32(cmdT.ExecuteScalar());

                using var cmdD = conn.CreateCommand();
                cmdD.CommandText = "SELECT COUNT(DISTINCT DocumentId) FROM DocumentVersions WHERE ProjectId = $p";
                cmdD.Parameters.AddWithValue("$p", projectId);
                resp.TotalDocuments = Convert.ToInt32(cmdD.ExecuteScalar());

                using var cmdS = conn.CreateCommand();
                cmdS.CommandText = "SELECT Status, COUNT(1) FROM Tasks WHERE ProjectId = $p GROUP BY Status";
                cmdS.Parameters.AddWithValue("$p", projectId);
                using var rS = cmdS.ExecuteReader();
                while (rS.Read()) { int s = rS.GetInt32(0); int c = rS.GetInt32(1); if (s == 0) resp.TasksTodo = c; else if (s == 1) resp.TasksInProgress = c; else if (s == 2) resp.TasksDone = c; }

                using var cmdU = conn.CreateCommand();
                cmdU.CommandText = @"
                    SELECT b.UserName, b.Amount
                    FROM Balances b
                    INNER JOIN ProjectMembers pm ON pm.UserName = b.UserName
                    WHERE pm.ProjectId = $p";
                cmdU.Parameters.AddWithValue("$p", projectId);
                using var rU = cmdU.ExecuteReader();
                while (rU.Read()) resp.UserReputation.Add(rU.GetString(0), rU.GetInt32(1));
            }
            catch { }

            resp.SecurityFindings = CountSecurityFindings(blocks);
            if (blocks.Count > 1)
            {
                var orderedBlocks = blocks.OrderBy(b => b.Index).ToList();
                var intervals = orderedBlocks
                    .Skip(1)
                    .Select((block, index) => Math.Abs((block.Timestamp - orderedBlocks[index].Timestamp).TotalSeconds))
                    .Where(seconds => seconds > 0)
                    .ToList();

                resp.AverageBlockIntervalSeconds = intervals.Count > 0 ? intervals.Average() : 0;
                var totalMinutes = Math.Max((orderedBlocks.Last().Timestamp - orderedBlocks.First().Timestamp).TotalMinutes, 1d / 60d);
                resp.BlocksPerMinute = Math.Round(orderedBlocks.Count / totalMinutes, 2);
            }

            return Task.FromResult(resp);
        }

        public override Task<DocumentResponse> GetProjectDocuments(ProjectRequest request, ServerCallContext context)
        {
            var response = new DocumentResponse();
            var db = new DatabaseManager(_dbFileName, _dbPassword);
            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!IsAuthorizedReadRequest(db, $"PROJECT:{projectId}:DOCUMENTS", userName, request.UserPublicKey, request.AuthSignature))
            {
                return Task.FromResult(response);
            }

            string role = db.GetUserRole(projectId, userName);
            if (role == "None" && projectId != "System")
            {
                return Task.FromResult(response);
            }

            try
            {
                using var conn = new SqliteConnection(DbConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT d.DocumentId, d.Title, d.Version, d.UpdatedBy, d.UpdatedAt, d.ContentHash, d.Content
                    FROM DocumentVersions d
                    INNER JOIN (
                        SELECT DocumentId, MAX(Version) AS LatestVersion
                        FROM DocumentVersions
                        WHERE ProjectId = $project
                        GROUP BY DocumentId
                    ) latest ON latest.DocumentId = d.DocumentId AND latest.LatestVersion = d.Version
                    WHERE d.ProjectId = $project
                    ORDER BY d.UpdatedAt DESC";
                cmd.Parameters.AddWithValue("$project", projectId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    response.Documents.Add(new DocumentSummary
                    {
                        DocumentId = reader.GetString(0),
                        Title = db.DecryptStoredValue(reader.GetString(1)),
                        LatestVersion = reader.GetInt32(2),
                        UpdatedBy = db.DecryptStoredValue(reader.GetString(3)),
                        UpdatedAt = reader.GetString(4),
                        ContentHash = reader.GetString(5),
                        Content = db.DecryptStoredValue(reader.GetString(6))
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DB Read Error in Documents");
            }

            return Task.FromResult(response);
        }

        public override Task<DocumentVersionResponse> GetDocumentVersions(DocumentHistoryRequest request, ServerCallContext context)
        {
            var response = new DocumentVersionResponse();
            var db = new DatabaseManager(_dbFileName, _dbPassword);
            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!IsAuthorizedReadRequest(db, $"PROJECT:{projectId}:DOCUMENT:{request.DocumentId}", userName, request.UserPublicKey, request.AuthSignature))
            {
                return Task.FromResult(response);
            }

            string role = db.GetUserRole(projectId, userName);
            if (role == "None" && projectId != "System")
            {
                return Task.FromResult(response);
            }

            try
            {
                using var conn = new SqliteConnection(DbConnectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT DocumentId, Version, Title, Content, ContentHash, UpdatedBy, UpdatedAt
                    FROM DocumentVersions
                    WHERE ProjectId = $project AND DocumentId = $document
                    ORDER BY Version DESC";
                cmd.Parameters.AddWithValue("$project", projectId);
                cmd.Parameters.AddWithValue("$document", request.DocumentId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    response.Versions.Add(new DocumentVersionItem
                    {
                        DocumentId = reader.GetString(0),
                        Version = reader.GetInt32(1),
                        Title = db.DecryptStoredValue(reader.GetString(2)),
                        Content = db.DecryptStoredValue(reader.GetString(3)),
                        ContentHash = reader.GetString(4),
                        UpdatedBy = db.DecryptStoredValue(reader.GetString(5)),
                        UpdatedAt = reader.GetString(6)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DB Read Error in DocumentVersions");
            }

            return Task.FromResult(response);
        }

        public override Task<SecurityAuditResponse> GetSecurityAudit(ProjectRequest request, ServerCallContext context)
        {
            var db = new DatabaseManager(_dbFileName, _dbPassword);
            string projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? "System" : request.ProjectId;
            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;

            if (!IsAuthorizedReadRequest(db, $"PROJECT:{projectId}:SECURITY_AUDIT", userName, request.UserPublicKey, request.AuthSignature))
            {
                return Task.FromResult(new SecurityAuditResponse
                {
                    ChainValid = false,
                    CheckedBlocks = 0
                });
            }

            string role = db.GetUserRole(projectId, userName);
            if (role == "None" && projectId != "System")
            {
                return Task.FromResult(new SecurityAuditResponse
                {
                    ChainValid = false,
                    CheckedBlocks = 0
                });
            }

            var blocks = db.LoadChain(projectId).OrderBy(b => b.Index).ToList();
            var response = new SecurityAuditResponse { CheckedBlocks = blocks.Count };

            for (int i = 0; i < blocks.Count; i++)
            {
                var current = blocks[i];

                if (current.Hash != current.CalculateHash())
                {
                    response.InvalidHashes++;
                    response.Items.Add(new SecurityAuditItem
                    {
                        Severity = "Critical",
                        CheckName = "Hash integrity",
                        Details = $"Block #{current.Index} hash does not match its content."
                    });
                }

                if (!current.VerifySignature())
                {
                    response.InvalidSignatures++;
                    response.Items.Add(new SecurityAuditItem
                    {
                        Severity = "Critical",
                        CheckName = "ECDSA signature",
                        Details = $"Block #{current.Index} signature is invalid."
                    });
                }

                if (!current.Hash.StartsWith(Blockchain.Core.Constants.NetworkParameters.TargetPrefix))
                {
                    response.InvalidProofOfWork++;
                    response.Items.Add(new SecurityAuditItem
                    {
                        Severity = "High",
                        CheckName = "Proof of work",
                        Details = $"Block #{current.Index} does not satisfy the target prefix."
                    });
                }

                string expectedPreviousHash = i == 0 ? "0" : blocks[i - 1].Hash;
                if (current.PreviousHash != expectedPreviousHash)
                {
                    response.BrokenLinks++;
                    response.Items.Add(new SecurityAuditItem
                    {
                        Severity = "Critical",
                        CheckName = "Chain linkage",
                        Details = $"Block #{current.Index} previous hash points to an unexpected parent."
                    });
                }
            }

            response.ChainValid = response.InvalidHashes == 0
                && response.InvalidSignatures == 0
                && response.InvalidProofOfWork == 0
                && response.BrokenLinks == 0;

            if (response.ChainValid)
            {
                response.Items.Add(new SecurityAuditItem
                {
                    Severity = "Info",
                    CheckName = "Audit result",
                    Details = "All checked blocks passed hash, signature, proof-of-work, and linkage validation."
                });
            }

            return Task.FromResult(response);
        }

        public override Task<ProjectListResponse> GetMyProjects(UserRequest request, ServerCallContext context)
        {
            var db = new DatabaseManager(_dbFileName, _dbPassword);
            var response = new ProjectListResponse();

            string userName = string.IsNullOrWhiteSpace(request.UserName) ? "Guest" : request.UserName;
            if (!IsAuthorizedReadRequest(db, $"USER:{userName}:PROJECTS", userName, request.UserPublicKey, request.AuthSignature))
            {
                return Task.FromResult(response);
            }

            var projects = db.GetUserProjects(userName);

            response.ProjectIds.AddRange(projects);

            return Task.FromResult(response);
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

        private static int CountSecurityFindings(List<Block> blocks)
        {
            int findings = 0;
            var orderedBlocks = blocks.OrderBy(b => b.Index).ToList();

            for (int i = 0; i < orderedBlocks.Count; i++)
            {
                var current = orderedBlocks[i];
                string expectedPreviousHash = i == 0 ? "0" : orderedBlocks[i - 1].Hash;

                if (current.Hash != current.CalculateHash()) findings++;
                if (!current.VerifySignature()) findings++;
                if (!current.Hash.StartsWith(Blockchain.Core.Constants.NetworkParameters.TargetPrefix)) findings++;
                if (current.PreviousHash != expectedPreviousHash) findings++;
            }

            return findings;
        }

        private static (int CommitCount, int ArtifactCount) CountProjectPayloads(List<Block> blocks)
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

        private async Task<int> SyncFromPeerAsync(string peerUrl)
        {
            int acceptedBlocks = 0;

            try
            {
                var db = new DatabaseManager(_dbFileName, _dbPassword);
                var channels = await _p2pService.FetchKnownChannelsAsync(peerUrl);
                if (!channels.Contains("System", StringComparer.OrdinalIgnoreCase))
                {
                    channels.Insert(0, "System");
                }

                foreach (var channelId in channels.OrderBy(channel => channel == "System" ? 0 : 1))
                {
                    var blocks = await _p2pService.FetchChainAsync(peerUrl, channelId);
                    var candidateChain = blocks.Select(ToBlock).OrderBy(block => block.Index).ToList();
                    if (_blockchainManager.TryAdoptChain(channelId, candidateChain))
                    {
                        acceptedBlocks += candidateChain.Count;
                        continue;
                    }

                    foreach (var block in candidateChain)
                    {
                        if (db.BlockExists(block.Hash, block.ChannelId))
                        {
                            continue;
                        }

                        if (_blockchainManager.ProcessPeerBlock(block))
                        {
                            acceptedBlocks++;
                        }
                    }
                }

                db.SavePeer(peerUrl);
            }
            catch (Exception ex)
            {
                var db = new DatabaseManager(_dbFileName, _dbPassword);
                db.MarkPeerFailure(peerUrl);
                _logger.LogWarning("[P2P] Initial sync from {PeerUrl} failed: {Message}", peerUrl, ex.Message);
            }

            return acceptedBlocks;
        }

        private async Task NotifyClientsAsync(BlockModel block)
        {
            if (_hubContext == null) return;

            string channelId = string.IsNullOrWhiteSpace(block.ChannelId) ? "System" : block.ChannelId;
            await _hubContext.Clients.Group(channelId).SendAsync("NewBlockBroadcast", block);

            if (!block.Data.Contains("\"Type\":\"Transfer\"")) return;

            using var doc = JsonDocument.Parse(block.Data);
            var root = doc.RootElement;
            if (root.TryGetProperty("TargetUser", out var targetUserProp) &&
                root.TryGetProperty("Amount", out var amountProp) &&
                root.TryGetProperty("User", out var senderProp))
            {
                string targetUser = targetUserProp.GetString() ?? "";
                int amount = amountProp.GetInt32();
                string sender = senderProp.GetString() ?? "Unknown";

                await _hubContext.Clients.Group($"USER_{targetUser}").SendAsync("FinancialTransferReceived", sender, amount);
            }
        }

        private static Block ToBlock(BlockModel model)
        {
            return new Block
            {
                Index = model.Index,
                Data = model.Data,
                PreviousHash = model.PreviousHash,
                Hash = model.Hash,
                Timestamp = DateTime.Parse(model.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind),
                ValidatorPublicKey = model.ValidatorPublicKey,
                Signature = model.Signature,
                Nonce = model.Nonce,
                ChannelId = string.IsNullOrWhiteSpace(model.ChannelId) ? "System" : model.ChannelId
            };
        }

        private bool IsSelfPeer(string peerUrl)
        {
            string? publicUrl = _configuration["P2P:PublicUrl"];
            return !string.IsNullOrWhiteSpace(publicUrl)
                && !string.IsNullOrWhiteSpace(peerUrl)
                && string.Equals(publicUrl.TrimEnd('/'), peerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
