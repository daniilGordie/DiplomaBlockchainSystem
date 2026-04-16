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

namespace Blockchain.Node.Services
{
    public class BlockchainGrpcService : BlockchainService.BlockchainServiceBase
    {
        private readonly ILogger<BlockchainGrpcService> _logger;
        private readonly BlockchainManager _blockchainManager;
        private readonly OracleIdentity _oracleIdentity;
        private readonly P2PNetworkService _p2pService;

        private static HashSet<string> _syncedCommits = new();
        private static HashSet<string> _syncedArtifacts = new();
        private static int _lastIndexedBlock = -1;
        private static readonly object _cacheLock = new object();

        private string DbConnectionString => "Data Source=nexus_node.db";

        public BlockchainGrpcService(
            ILogger<BlockchainGrpcService> logger,
            BlockchainManager manager,
            OracleIdentity oracleIdentity,
            P2PNetworkService p2pService)
        {
            _logger = logger;
            _blockchainManager = manager;
            _oracleIdentity = oracleIdentity;
            _p2pService = p2pService;
        }

        public override Task<StatusReply> AddPeer(PeerRequest request, ServerCallContext context)
        {
            _p2pService.AddPeer(request.Url);
            return Task.FromResult(new StatusReply { Success = true, Message = "Peer added" });
        }

        public override Task<PeerListResponse> GetPeers(EmptyRequest request, ServerCallContext context)
        {
            var resp = new PeerListResponse();
            resp.Urls.AddRange(_p2pService.GetPeers());
            return Task.FromResult(resp);
        }

        public override Task<StatusReply> BroadcastBlock(BlockModel request, ServerCallContext context)
        {
            var peerBlock = new Block
            {
                Index = request.Index,
                Data = request.Data,
                PreviousHash = request.PreviousHash,
                Hash = request.Hash,
                Timestamp = DateTime.Parse(request.Timestamp),
                ValidatorPublicKey = request.ValidatorPublicKey,
                Signature = request.Signature,
                Nonce = request.Nonce
            };

            bool isAccepted = _blockchainManager.ProcessPeerBlock(peerBlock);

            return Task.FromResult(new StatusReply { Success = isAccepted, Message = isAccepted ? "Accepted" : "Rejected" });
        }

        public override Task<StatusReply> ReceiveBlock(BlockModel request, ServerCallContext context)
        {
            var db = new Blockchain.Core.DatabaseManager();
            string targetChannel = "System";

            try
            {
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var taskEvent = System.Text.Json.JsonSerializer.Deserialize<Blockchain.Core.Contracts.ContractTaskEvent>(request.Data, options);

                if (taskEvent != null && !string.IsNullOrEmpty(taskEvent.ProjectId))
                {
                    if (taskEvent.Type != "CreateProject" && taskEvent.Type != "AssignRole")
                    {
                        targetChannel = taskEvent.ProjectId;
                        if (!db.IsProjectExists(taskEvent.ProjectId))
                            return Task.FromResult(new StatusReply { Success = false, Message = "Project does not exist." });
                    }
                }
            }
            catch { } 

            var channelBlocks = db.LoadChain(targetChannel);
            string expectedPrevHash = channelBlocks.Count > 0 ? channelBlocks.Last().Hash : "0";
            int expectedIndex = channelBlocks.Count;

            if (request.PreviousHash != expectedPrevHash)
            {
                _logger.LogWarning($"[Chain Mismatch] Channel: {targetChannel}. Expected: {expectedPrevHash}, Got: {request.PreviousHash}");
                return Task.FromResult(new StatusReply { Success = false, Message = "Chain mismatch (PreviousHash)" });
            }

            string rawData = $"{request.PreviousHash}{request.Timestamp}{request.Data}{request.ValidatorPublicKey}{request.Nonce}";
            string calculatedHash;
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawData));
                calculatedHash = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }

            if (calculatedHash != request.Hash.ToLowerInvariant() || !calculatedHash.StartsWith(Blockchain.Core.Constants.ConsensusRules.TargetPrefix))
            {
                return Task.FromResult(new StatusReply { Success = false, Message = "Invalid PoW" });
            }

            var newBlock = new Blockchain.Core.Block
            {
                Index = expectedIndex,
                Data = request.Data,
                PreviousHash = request.PreviousHash,
                Hash = request.Hash,
                Timestamp = DateTime.Parse(request.Timestamp),
                ValidatorPublicKey = request.ValidatorPublicKey,
                Signature = request.Signature,
                Nonce = request.Nonce,
                ChannelId = targetChannel
            };

            bool isAccepted = _blockchainManager.AddBlock(newBlock);

            if (isAccepted)
            {
                _logger.LogInformation($"[Node Router] Block {newBlock.Hash.Substring(0, 8)}... accepted in channel: {targetChannel}");
                try { _p2pService.BroadcastBlockAsync(newBlock); } catch { }
                return Task.FromResult(new StatusReply { Success = true, Message = $"Block accepted in channel: {targetChannel}" });
            }
            else
            {
                return Task.FromResult(new StatusReply { Success = false, Message = "Access Denied by Smart Contract." });
            }
        }

        public override Task<BlockModel> GetLastBlock(EmptyRequest request, ServerCallContext context)
        {
            var b = _blockchainManager.GetLatestBlock();
            return Task.FromResult(new BlockModel { Index = b.Index, Data = b.Data, Hash = b.Hash, PreviousHash = b.PreviousHash, Timestamp = b.Timestamp.ToString("O"), ValidatorPublicKey = b.ValidatorPublicKey, Signature = b.Signature, Nonce = b.Nonce });
        }

        public override Task<ChainResponse> GetChain(ChainRequest request, ServerCallContext context)
        {
            var response = new ChainResponse();
            var db = new Blockchain.Core.DatabaseManager();

            string channelToRead = string.IsNullOrEmpty(request.ChannelId) ? "System" : request.ChannelId;
            string userName = string.IsNullOrEmpty(request.UserName) ? "Guest" : request.UserName;

            string role = db.GetUserRole(channelToRead, userName);
            //if (role == "None" && channelToRead != "System")
            //{
            //    return Task.FromResult(response); 
            //}

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
                    ValidatorPublicKey = block.ValidatorPublicKey,
                    Signature = block.Signature ?? "",
                    Nonce = block.Nonce,
                    ChannelId = block.ChannelId
                });
            }

            return Task.FromResult(response);
        }

        public override Task<TaskResponse> GetProjectTasks(ProjectRequest request, ServerCallContext context)
        {
            var response = new TaskResponse(); 
            var db = new Blockchain.Core.DatabaseManager();

            string role = db.GetUserRole(request.ProjectId, request.UserName);
            response.UserRole = role;

            //if (role == "None" && request.ProjectId != "System")
            //{
            //    _logger.LogWarning($"[Security] Read-Access Denied for {request.UserName} to {request.ProjectId}");
            //    return Task.FromResult(response);
            //}

            try
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.DbFileName}");
                conn.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = "SELECT TaskId, Title, Creator, Assignee, Status, ProjectId, Description, ParentTaskId, BranchInfo FROM Tasks WHERE ProjectId = $p OR $p = ''";
                cmd.Parameters.AddWithValue("$p", request.ProjectId ?? "Alpha");

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    response.Tasks.Add(new TaskItem 
                    {
                        Id = reader.GetString(0),
                        Title = reader.GetString(1),
                        Creator = reader.GetString(2),
                        Assignee = reader.GetString(3),
                        Status = reader.GetInt32(4),
                        ProjectId = reader.GetString(5),
                        Description = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        ParentTaskId = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        BranchInfo = reader.IsDBNull(8) ? "" : reader.GetString(8)
                    });
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "DB Read Error in Tasks"); }

            return Task.FromResult(response);
        }

        public override Task<TaskHistoryResponse> GetTaskHistory(TaskHistoryRequest request, ServerCallContext context)
        {
            var response = new TaskHistoryResponse();
            var blocks = _blockchainManager.Chain.Where(b => b.Data.Contains(request.TaskId)).ToList();

            foreach (var b in blocks)
            {
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

        public override async Task<StatusReply> RegisterArtifact(IAsyncStreamReader<ArtifactChunk> requestStream, ServerCallContext context)
        {
            string fileName = ""; string owner = "";
            using var sha256 = System.Security.Cryptography.SHA256.Create();

            while (await requestStream.MoveNext())
            {
                var chunk = requestStream.Current;
                if (string.IsNullOrEmpty(fileName)) { fileName = chunk.FileName; owner = chunk.Owner; }
                sha256.TransformBlock(chunk.Data.ToByteArray(), 0, chunk.Data.Length, null, 0);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            string fileHash = BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLower();

            if (_syncedArtifacts.Contains(fileHash)) return new StatusReply { Success = false, Message = "Artifact already registered." };

            string payload = JsonSerializer.Serialize(new { Source = "ArtifactRegistry", Type = "Register", FileName = fileName, FileHash = fileHash, RegisteredBy = owner, VerificationMethod = "Oracle Node" });
            string signature = _oracleIdentity.SignData(payload);
            var block = new Block { Data = payload, ValidatorPublicKey = _oracleIdentity.PublicKey, Signature = signature };

            if (_blockchainManager.AddBlock(block)) { _syncedArtifacts.Add(fileHash); return new StatusReply { Success = true, Message = $"Artifact {fileName} secured. Hash: {fileHash}" }; }
            return new StatusReply { Success = false, Message = "Failed to register artifact." };
        }

        public override Task<AnalyticsResponse> GetAnalytics(EmptyRequest request, ServerCallContext context)
        {
            var resp = new AnalyticsResponse { TotalBlocks = _blockchainManager.Chain.Count, TotalCommits = _syncedCommits.Count, TotalArtifacts = _syncedArtifacts.Count };
            try
            {
                using var conn = new SqliteConnection(DbConnectionString);
                conn.Open();

                using var cmdT = conn.CreateCommand(); cmdT.CommandText = "SELECT COUNT(1) FROM Tasks";
                resp.TotalTasks = Convert.ToInt32(cmdT.ExecuteScalar());

                using var cmdS = conn.CreateCommand(); cmdS.CommandText = "SELECT Status, COUNT(1) FROM Tasks GROUP BY Status";
                using var rS = cmdS.ExecuteReader();
                while (rS.Read()) { int s = rS.GetInt32(0); int c = rS.GetInt32(1); if (s == 0) resp.TasksTodo = c; else if (s == 1) resp.TasksInProgress = c; else if (s == 2) resp.TasksDone = c; }

                using var cmdU = conn.CreateCommand(); cmdU.CommandText = "SELECT UserName, Amount FROM Balances";
                using var rU = cmdU.ExecuteReader();
                while (rU.Read()) resp.UserReputation.Add(rU.GetString(0), rU.GetInt32(1));
            }
            catch { }
            return Task.FromResult(resp);
        }

        public override Task<ProjectListResponse> GetMyProjects(UserRequest request, ServerCallContext context)
        {
            var db = new Blockchain.Core.DatabaseManager();
            var projects = db.GetUserProjects(request.UserName);

            var response = new ProjectListResponse();
            response.ProjectIds.AddRange(projects); 

            return Task.FromResult(response);
        }
    }
}