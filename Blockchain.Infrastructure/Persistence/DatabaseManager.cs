using Microsoft.Data.Sqlite;
using Blockchain.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

    namespace Blockchain.Infrastructure.Persistence
    {
        public class DatabaseManager : IBlockchainStore, IPeerStore
        {
            public string DbFileName { get; private set; }
            private readonly string _dbPassword;
            private readonly byte[]? _fieldEncryptionKey;
            private const string EncryptedPrefix = "enc:v1:";
            private const int EncryptionTagSize = 16;

            private string ConnectionString => string.IsNullOrEmpty(_dbPassword)
                ? $"Data Source={DbFileName}"
                : $"Data Source={DbFileName};Password={_dbPassword}";

            public DatabaseManager(string dbName, string dbPassword = "")
            {
                if (string.IsNullOrWhiteSpace(dbName))
                {
                    throw new ArgumentException("Database name cannot be empty!", nameof(dbName));
                }

                DbFileName = dbName;
                _dbPassword = dbPassword;
                _fieldEncryptionKey = string.IsNullOrWhiteSpace(_dbPassword)
                    ? null
                    : SHA256.HashData(Encoding.UTF8.GetBytes($"{_dbPassword}|nexus-field-encryption|{DbFileName}"));
                InitializeDatabase();
            }

            private bool IsFieldEncryptionEnabled => _fieldEncryptionKey is { Length: > 0 };

            private string EncryptString(string? value)
            {
                if (string.IsNullOrEmpty(value) || !IsFieldEncryptionEnabled || value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
                {
                    return value ?? string.Empty;
                }

                byte[] nonce = RandomNumberGenerator.GetBytes(12);
                byte[] plaintext = Encoding.UTF8.GetBytes(value);
                byte[] ciphertext = new byte[plaintext.Length];
                byte[] tag = new byte[EncryptionTagSize];

                using var aes = new AesGcm(_fieldEncryptionKey!, EncryptionTagSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag);

                byte[] payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
                Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
                Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
                Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);

                return EncryptedPrefix + Convert.ToBase64String(payload);
            }

            private string DecryptString(string? value)
            {
                if (string.IsNullOrEmpty(value) || !value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
                {
                    return value ?? string.Empty;
                }

                if (!IsFieldEncryptionEnabled)
                {
                    return string.Empty;
                }

                try
                {
                    byte[] payload = Convert.FromBase64String(value.Substring(EncryptedPrefix.Length));
                    if (payload.Length < 28)
                    {
                        return string.Empty;
                    }

                    byte[] nonce = payload[..12];
                    byte[] tag = payload[12..28];
                    byte[] ciphertext = payload[28..];
                    byte[] plaintext = new byte[ciphertext.Length];

                    using var aes = new AesGcm(_fieldEncryptionKey!, EncryptionTagSize);
                    aes.Decrypt(nonce, ciphertext, tag, plaintext);
                    return Encoding.UTF8.GetString(plaintext);
                }
                catch
                {
                    return string.Empty;
                }
            }

            public string DecryptStoredValue(string? value) => DecryptString(value);

            public void InitializeDatabase()
            {
                using (var connection = new SqliteConnection(ConnectionString))
                {
                    connection.Open();
                    var pragma = connection.CreateCommand();
                    pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                    pragma.ExecuteNonQuery();

                    var cmd = connection.CreateCommand();

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Blocks (
                        IndexId INTEGER PRIMARY KEY, Timestamp TEXT, Data TEXT, PreviousHash TEXT, 
                        Hash TEXT UNIQUE, ValidatorPublicKey TEXT, Signature TEXT, Nonce INTEGER
                    );
                    CREATE INDEX IF NOT EXISTS idx_hash ON Blocks(Hash);";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Tasks (
                        TaskId TEXT PRIMARY KEY, Title TEXT, Status INTEGER, 
                        Creator TEXT, Assignee TEXT, ProjectId TEXT,
                        Description TEXT, ParentTaskId TEXT, BranchInfo TEXT
                    );";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS ProjectMembers (ProjectId TEXT, UserName TEXT, Role TEXT, PRIMARY KEY (ProjectId, UserName));";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Balances (UserName TEXT PRIMARY KEY, Amount INTEGER);";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Mempool (TxId TEXT PRIMARY KEY, PayloadJson TEXT, Timestamp TEXT);";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Users (UserName TEXT PRIMARY KEY, PublicKey TEXT);";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS GovernanceProposals (
                        ProposalId TEXT PRIMARY KEY,
                        ProjectId TEXT NOT NULL,
                        Title TEXT NOT NULL,
                        Description TEXT,
                        CreatedBy TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        YesVotes INTEGER NOT NULL DEFAULT 0,
                        NoVotes INTEGER NOT NULL DEFAULT 0
                    );";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS GovernanceVotes (
                        ProposalId TEXT NOT NULL,
                        UserName TEXT NOT NULL,
                        Vote INTEGER NOT NULL,
                        PRIMARY KEY (ProposalId, UserName)
                    );";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS DocumentVersions (
                        ProjectId TEXT NOT NULL,
                        DocumentId TEXT NOT NULL,
                        Version INTEGER NOT NULL,
                        Title TEXT NOT NULL,
                        Content TEXT NOT NULL,
                        ContentHash TEXT NOT NULL,
                        UpdatedBy TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        PRIMARY KEY (ProjectId, DocumentId, Version)
                    );
                    CREATE INDEX IF NOT EXISTS idx_document_versions_project ON DocumentVersions(ProjectId, DocumentId, Version);";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS PendingBlocks (
                        Hash TEXT PRIMARY KEY,
                        ChannelId TEXT NOT NULL,
                        IndexId INTEGER NOT NULL,
                        Timestamp TEXT NOT NULL,
                        Data TEXT NOT NULL,
                        PreviousHash TEXT NOT NULL,
                        ValidatorPublicKey TEXT,
                        Signature TEXT,
                        Nonce INTEGER NOT NULL,
                        Reason TEXT,
                        ReceivedAt TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_pending_prev ON PendingBlocks(ChannelId, PreviousHash);";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Peers (
                        Url TEXT PRIMARY KEY,
                        NodeId TEXT,
                        Role TEXT,
                        LastSeen TEXT,
                        LastFailure TEXT,
                        IsTrusted INTEGER NOT NULL DEFAULT 1
                    );";
                    cmd.ExecuteNonQuery();
                    EnsurePeerColumns(connection);
                }
            }

            public static string SanitizeChannelName(string channelId)
            {
                return ChannelName.Normalize(channelId);
            }

            private void CreateChannelTableInternal(SqliteConnection conn, SqliteTransaction tx, string channelId)
            {
                string safeChannel = SanitizeChannelName(channelId);
                if (safeChannel == "System") return;

                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS Blocks_{safeChannel} (
                    IndexId INTEGER PRIMARY KEY, Timestamp TEXT, Data TEXT, PreviousHash TEXT, 
                    Hash TEXT UNIQUE, ValidatorPublicKey TEXT, Signature TEXT, Nonce INTEGER
                );
                CREATE INDEX IF NOT EXISTS idx_hash_{safeChannel} ON Blocks_{safeChannel}(Hash);";
                cmd.ExecuteNonQuery();
            }

            public static void EnsureInitialized(string dbName = "nexus_node.db")
            {
                new DatabaseManager(dbName);
            }

            public void SaveBlock(Block block, string channelId = "System")
            {
                using (var connection = new SqliteConnection(ConnectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            string safeChannel = SanitizeChannelName(channelId);
                            string tableName = safeChannel == "System" ? "Blocks" : $"Blocks_{safeChannel}";

                            if (safeChannel != "System")
                            {
                                CreateChannelTableInternal(connection, transaction, safeChannel);
                            }

                            var cmdBlock = connection.CreateCommand();
                            cmdBlock.Transaction = transaction;

                            cmdBlock.CommandText = $"INSERT INTO {tableName} (IndexId, Timestamp, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce) VALUES ($idx, $time, $data, $prev, $hash, $val, $sig, $nonce)";
                            cmdBlock.Parameters.AddWithValue("$idx", block.Index);
                            cmdBlock.Parameters.AddWithValue("$time", block.Timestamp.ToString("O"));
                            cmdBlock.Parameters.AddWithValue("$data", EncryptString(block.Data));
                            cmdBlock.Parameters.AddWithValue("$prev", block.PreviousHash);
                            cmdBlock.Parameters.AddWithValue("$hash", block.Hash);
                            cmdBlock.Parameters.AddWithValue("$val", EncryptString(block.ValidatorPublicKey ?? ""));
                            cmdBlock.Parameters.AddWithValue("$sig", EncryptString(block.Signature ?? ""));
                            cmdBlock.Parameters.AddWithValue("$nonce", block.Nonce);
                            cmdBlock.ExecuteNonQuery();

                            if (block.Data.Trim().StartsWith("{") || block.Data.Trim().StartsWith("["))
                            {
                                using var doc = JsonDocument.Parse(block.Data);
                                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                                {
                                    UpdateStateIndex(connection, transaction, doc.RootElement, block.ValidatorPublicKey ?? "");
                                }
                            }

                            transaction.Commit();
                        }
                        catch (Exception ex)
                        {
                            transaction.Rollback();
                            Console.WriteLine($"\n[DB ERROR] Error saving block/state: {ex.Message}\n");
                            throw;
                        }
                    }
                }
            }

            private JsonElement? GetPropertyCI(JsonElement root, string name)
            {
                if (root.TryGetProperty(name, out var val)) return val;
                string camelCase = char.ToLowerInvariant(name[0]) + name.Substring(1);
                if (root.TryGetProperty(camelCase, out val)) return val;
                return null;
            }

            private string GetStringSafe(JsonElement root, string name, string def = "")
            {
                var prop = GetPropertyCI(root, name);
                if (prop == null || prop.Value.ValueKind == JsonValueKind.Null) return def;
                return prop.Value.ToString() ?? def;
            }

            private int GetIntSafe(JsonElement root, string name, int def = 0)
            {
                var prop = GetPropertyCI(root, name);
                if (prop == null || prop.Value.ValueKind == JsonValueKind.Null) return def;
                if (prop.Value.ValueKind == JsonValueKind.Number) return prop.Value.GetInt32();
                if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), out int val)) return val;
                return def;
            }

            private void UpdateStateIndex(SqliteConnection conn, SqliteTransaction tx, JsonElement root, string validatorPubKey)
            {
                string type = GetStringSafe(root, "Type");
                if (string.IsNullOrEmpty(type)) return;

                string sender = GetStringSafe(root, "User", "Anon");

                if (!string.IsNullOrEmpty(sender) && sender != "Anon" && !string.IsNullOrEmpty(validatorPubKey))
                {
                    var cmdUser = conn.CreateCommand();
                    cmdUser.Transaction = tx;
                    cmdUser.CommandText = "INSERT OR IGNORE INTO Users (UserName, PublicKey) VALUES ($u, $pk)";
                    cmdUser.Parameters.AddWithValue("$u", sender);
                    cmdUser.Parameters.AddWithValue("$pk", EncryptString(validatorPubKey));
                    cmdUser.ExecuteNonQuery();
                }

                if (type == "AssignRole" || type == "CreateProject")
                {
                    string projRole = GetStringSafe(root, "ProjectId", "System");

                    if (type == "CreateProject")
                    {
                        CreateChannelTableInternal(conn, tx, projRole);
                    }

                    if (!IsProjectExistsInternal(conn, tx, projRole))
                    {
                        var cmdOwner = conn.CreateCommand();
                        cmdOwner.Transaction = tx;
                        cmdOwner.CommandText = "INSERT INTO ProjectMembers (ProjectId, UserName, Role) VALUES ($p, $u, $r)";
                        cmdOwner.Parameters.AddWithValue("$p", projRole);
                        cmdOwner.Parameters.AddWithValue("$u", sender);
                        cmdOwner.Parameters.AddWithValue("$r", EncryptString("Owner"));
                        cmdOwner.ExecuteNonQuery();
                    }

                    if (type == "AssignRole")
                    {
                        string target = GetStringSafe(root, "TargetUser");
                        string targetPublicKey = GetStringSafe(root, "TargetPublicKey");
                        string role = GetStringSafe(root, "Role", "Worker");
                        if (!string.IsNullOrEmpty(target))
                        {
                            var cmdRole = conn.CreateCommand();
                            cmdRole.Transaction = tx;
                            cmdRole.CommandText = "INSERT OR REPLACE INTO ProjectMembers (ProjectId, UserName, Role) VALUES ($p, $u, $r)";
                            cmdRole.Parameters.AddWithValue("$p", projRole);
                            cmdRole.Parameters.AddWithValue("$u", target);
                            cmdRole.Parameters.AddWithValue("$r", EncryptString(role));
                            cmdRole.ExecuteNonQuery();

                            if (!string.IsNullOrWhiteSpace(targetPublicKey))
                            {
                                var cmdUserBind = conn.CreateCommand();
                                cmdUserBind.Transaction = tx;
                                cmdUserBind.CommandText = "INSERT OR REPLACE INTO Users (UserName, PublicKey) VALUES ($u, $pk)";
                                cmdUserBind.Parameters.AddWithValue("$u", target);
                                cmdUserBind.Parameters.AddWithValue("$pk", EncryptString(targetPublicKey));
                                cmdUserBind.ExecuteNonQuery();
                            }
                        }
                    }
                }

                string tid = GetStringSafe(root, "TaskId");
                if ((type == "Create" || type == "Update" || type == "Move") && !string.IsNullOrEmpty(tid))
                {
                    string proj = GetStringSafe(root, "ProjectId", "System");
                    string title = GetStringSafe(root, "Title", "No Title");
                    int status = GetIntSafe(root, "Status", 0);
                    string desc = GetStringSafe(root, "Description");
                    string parent = GetStringSafe(root, "ParentTaskId");
                    string branch = GetStringSafe(root, "BranchInfo");
                    string assignee = GetStringSafe(root, "Assignee", "None");

                    var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;

                    if (type == "Create")
                    {
                        cmd.CommandText = @"INSERT OR REPLACE INTO Tasks 
                        (TaskId, Title, Status, Creator, Assignee, ProjectId, Description, ParentTaskId, BranchInfo) 
                        VALUES ($id, $title, $st, $creator, $assignee, $proj, $desc, $parent, $branch)";
                        cmd.Parameters.AddWithValue("$creator", EncryptString(sender));

                        AddBalanceInternal(conn, tx, sender, 5);
                    }
                    else if (type == "Update" || type == "Move")
                    {
                        cmd.CommandText = @"UPDATE Tasks 
                        SET Title = $title, Status = $st, Assignee = $assignee, 
                            Description = $desc, ParentTaskId = $parent, BranchInfo = $branch 
                        WHERE TaskId = $id";

                        if (status == 2) AddBalanceInternal(conn, tx, sender, 20);
                    }

                    cmd.Parameters.AddWithValue("$id", tid);
                    cmd.Parameters.AddWithValue("$title", EncryptString(title));
                    cmd.Parameters.AddWithValue("$st", status);
                    cmd.Parameters.AddWithValue("$assignee", EncryptString(string.IsNullOrEmpty(assignee) ? "None" : assignee));
                    cmd.Parameters.AddWithValue("$proj", proj);
                    cmd.Parameters.AddWithValue("$desc", EncryptString(desc));
                    cmd.Parameters.AddWithValue("$parent", EncryptString(parent));
                    cmd.Parameters.AddWithValue("$branch", EncryptString(branch));
                    cmd.ExecuteNonQuery();
                }

                if (type == "CreateDocument" || type == "UpdateDocument")
                {
                    string projectId = GetStringSafe(root, "ProjectId", "System");
                    string documentId = GetStringSafe(root, "DocumentId");
                    string title = GetStringSafe(root, "Title", "Untitled document");
                    string content = GetStringSafe(root, "Content");
                    string updatedAt = GetStringSafe(root, "Timestamp", DateTime.UtcNow.ToString("O"));

                    if (!string.IsNullOrWhiteSpace(documentId) && projectId != "System")
                    {
                        int nextVersion = 1;
                        var cmdVersion = conn.CreateCommand();
                        cmdVersion.Transaction = tx;
                        cmdVersion.CommandText = @"
                        SELECT COALESCE(MAX(Version), 0) + 1
                        FROM DocumentVersions
                        WHERE ProjectId = $project AND DocumentId = $document";
                        cmdVersion.Parameters.AddWithValue("$project", projectId);
                        cmdVersion.Parameters.AddWithValue("$document", documentId);
                        nextVersion = Convert.ToInt32(cmdVersion.ExecuteScalar());

                        var cmdDocument = conn.CreateCommand();
                        cmdDocument.Transaction = tx;
                        cmdDocument.CommandText = @"
                        INSERT OR IGNORE INTO DocumentVersions
                        (ProjectId, DocumentId, Version, Title, Content, ContentHash, UpdatedBy, UpdatedAt)
                        VALUES ($project, $document, $version, $title, $content, $hash, $user, $updatedAt)";
                        cmdDocument.Parameters.AddWithValue("$project", projectId);
                        cmdDocument.Parameters.AddWithValue("$document", documentId);
                        cmdDocument.Parameters.AddWithValue("$version", nextVersion);
                        cmdDocument.Parameters.AddWithValue("$title", EncryptString(title));
                        cmdDocument.Parameters.AddWithValue("$content", EncryptString(content));
                        cmdDocument.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant());
                        cmdDocument.Parameters.AddWithValue("$user", EncryptString(sender));
                        cmdDocument.Parameters.AddWithValue("$updatedAt", updatedAt);
                        cmdDocument.ExecuteNonQuery();
                    }
                }

                if (type == "Transfer")
                {
                    string target = GetStringSafe(root, "TargetUser");
                    int amount = GetIntSafe(root, "Amount", 0);

                    if (!string.IsNullOrEmpty(target) && amount > 0)
                    {
                        AddBalanceInternal(conn, tx, sender, -amount);
                        AddBalanceInternal(conn, tx, target, amount);
                    }
                }

                if (type == "CreateProposal")
                {
                    string proposalId = GetStringSafe(root, "ProposalId");
                    string projectId = GetStringSafe(root, "ProjectId", "System");
                    string title = GetStringSafe(root, "Title", "Untitled proposal");
                    string description = GetStringSafe(root, "Description");
                    string createdAtRaw = GetStringSafe(root, "Timestamp", DateTime.UtcNow.ToString("O"));
                    string createdAt = DateTime.TryParse(createdAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedCreatedAt)
                        ? parsedCreatedAt.ToUniversalTime().ToString("O")
                        : DateTime.UtcNow.ToString("O");

                    if (!string.IsNullOrWhiteSpace(proposalId) && projectId != "System")
                    {
                        var cmdProposal = conn.CreateCommand();
                        cmdProposal.Transaction = tx;
                        cmdProposal.CommandText = @"
                        INSERT OR IGNORE INTO GovernanceProposals
                        (ProposalId, ProjectId, Title, Description, CreatedBy, CreatedAt, Status, YesVotes, NoVotes)
                        VALUES ($id, $project, $title, $description, $createdBy, $createdAt, 'Open', 0, 0)";
                        cmdProposal.Parameters.AddWithValue("$id", proposalId);
                        cmdProposal.Parameters.AddWithValue("$project", projectId);
                        cmdProposal.Parameters.AddWithValue("$title", EncryptString(title));
                        cmdProposal.Parameters.AddWithValue("$description", EncryptString(description));
                        cmdProposal.Parameters.AddWithValue("$createdBy", EncryptString(sender));
                        cmdProposal.Parameters.AddWithValue("$createdAt", createdAt);
                        cmdProposal.ExecuteNonQuery();
                    }
                }

                if (type == "CastVote")
                {
                    string proposalId = GetStringSafe(root, "ProposalId");
                    string voteText = GetStringSafe(root, "Vote");
                    bool voteYes = voteText.Equals("true", StringComparison.OrdinalIgnoreCase) || voteText == "1" || voteText.Equals("yes", StringComparison.OrdinalIgnoreCase);

                    if (!string.IsNullOrWhiteSpace(proposalId) && !string.IsNullOrWhiteSpace(sender))
                    {
                        var proposalMeta = GetProposalMetaInternal(conn, tx, proposalId);
                        if (proposalMeta == null)
                        {
                            return;
                        }

                        if (!string.Equals(proposalMeta.Value.Status, "Open", StringComparison.OrdinalIgnoreCase) ||
                            IsVotingClosed(proposalMeta.Value.CreatedAt))
                        {
                            RecalculateProposalVoteState(conn, tx, proposalId);
                            return;
                        }

                        var cmdVote = conn.CreateCommand();
                        cmdVote.Transaction = tx;
                        cmdVote.CommandText = @"
                        INSERT OR REPLACE INTO GovernanceVotes (ProposalId, UserName, Vote)
                        VALUES ($proposal, $user, $vote)";
                        cmdVote.Parameters.AddWithValue("$proposal", proposalId);
                        cmdVote.Parameters.AddWithValue("$user", sender);
                        cmdVote.Parameters.AddWithValue("$vote", voteYes ? 1 : 0);
                        cmdVote.ExecuteNonQuery();

                        RecalculateProposalVoteState(conn, tx, proposalId);
                    }
                }
            }

            private void RecalculateProposalVoteState(SqliteConnection conn, SqliteTransaction tx, string proposalId)
            {
                var proposalMeta = GetProposalMetaInternal(conn, tx, proposalId);
                if (proposalMeta == null) return;

                string projectId = proposalMeta.Value.ProjectId;
                bool votingClosed = IsVotingClosed(proposalMeta.Value.CreatedAt);

                var cmdMembers = conn.CreateCommand();
                cmdMembers.Transaction = tx;
                cmdMembers.CommandText = "SELECT COUNT(1) FROM ProjectMembers WHERE ProjectId = $project";
                cmdMembers.Parameters.AddWithValue("$project", projectId);
                int eligibleVoterCount = Convert.ToInt32(cmdMembers.ExecuteScalar());

                var cmdCounts = conn.CreateCommand();
                cmdCounts.Transaction = tx;
                cmdCounts.CommandText = @"
                SELECT
                    SUM(CASE WHEN Vote = 1 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Vote = 0 THEN 1 ELSE 0 END)
                FROM GovernanceVotes
                WHERE ProposalId = $proposal";
                cmdCounts.Parameters.AddWithValue("$proposal", proposalId);

                int yesVotes = 0;
                int noVotes = 0;
                using (var reader = cmdCounts.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        yesVotes = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                        noVotes = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                    }
                }

                string status = "Open";
                int totalVotes = yesVotes + noVotes;
                bool allMembersVoted = eligibleVoterCount > 0 && totalVotes >= eligibleVoterCount;
                if (votingClosed || allMembersVoted)
                {
                    bool approved = yesVotes >= noVotes;
                    status = approved ? "Approved" : "Rejected";
                }

                var cmdUpdate = conn.CreateCommand();
                cmdUpdate.Transaction = tx;
                cmdUpdate.CommandText = @"
                UPDATE GovernanceProposals
                SET YesVotes = $yes, NoVotes = $no, Status = $status
                WHERE ProposalId = $proposal";
                cmdUpdate.Parameters.AddWithValue("$yes", yesVotes);
                cmdUpdate.Parameters.AddWithValue("$no", noVotes);
                cmdUpdate.Parameters.AddWithValue("$status", status);
                cmdUpdate.Parameters.AddWithValue("$proposal", proposalId);
                cmdUpdate.ExecuteNonQuery();
            }

            public void RefreshGovernanceStates(string projectId)
            {
                if (string.IsNullOrWhiteSpace(projectId) || projectId == "System")
                {
                    return;
                }

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var tx = connection.BeginTransaction();

                var proposalIds = new List<string>();
                var cmdOpen = connection.CreateCommand();
                cmdOpen.Transaction = tx;
                cmdOpen.CommandText = @"
                SELECT ProposalId
                FROM GovernanceProposals
                WHERE ProjectId = $project AND Status = 'Open'";
                cmdOpen.Parameters.AddWithValue("$project", projectId);

                using (var reader = cmdOpen.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        proposalIds.Add(reader.GetString(0));
                    }
                }

                foreach (string openProposalId in proposalIds)
                {
                    RecalculateProposalVoteState(connection, tx, openProposalId);
                }

                tx.Commit();
            }

            public string GetProposalCreator(string proposalId)
            {
                if (string.IsNullOrWhiteSpace(proposalId)) return string.Empty;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT CreatedBy FROM GovernanceProposals WHERE ProposalId = $proposal LIMIT 1";
                cmd.Parameters.AddWithValue("$proposal", proposalId);
                return DecryptString(cmd.ExecuteScalar()?.ToString());
            }

            private (string ProjectId, string CreatedBy, string CreatedAt, string Status)? GetProposalMetaInternal(SqliteConnection conn, SqliteTransaction tx, string proposalId)
            {
                var cmdProposal = conn.CreateCommand();
                cmdProposal.Transaction = tx;
                cmdProposal.CommandText = @"
                SELECT ProjectId, CreatedBy, CreatedAt, Status
                FROM GovernanceProposals
                WHERE ProposalId = $proposal
                LIMIT 1";
                cmdProposal.Parameters.AddWithValue("$proposal", proposalId);

                using var reader = cmdProposal.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                string projectId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                string createdBy = reader.IsDBNull(1) ? "" : DecryptString(reader.GetString(1));
                string createdAt = reader.IsDBNull(2) ? DateTime.UtcNow.ToString("O") : reader.GetString(2);
                string status = reader.IsDBNull(3) ? "Open" : reader.GetString(3);
                return (projectId, createdBy, createdAt, status);
            }

            private static bool IsVotingClosed(string createdAtRaw)
            {
                if (!DateTime.TryParse(createdAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
                {
                    return false;
                }

                DateTime votingEndsAt = createdAt.ToUniversalTime().AddHours(1);
                return DateTime.UtcNow >= votingEndsAt;
            }

            private void AddBalanceInternal(SqliteConnection conn, SqliteTransaction tx, string user, int amount)
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                INSERT OR REPLACE INTO Balances (UserName, Amount) 
                VALUES ($u, COALESCE((SELECT Amount FROM Balances WHERE UserName = $u), 0) + $a)";
                cmd.Parameters.AddWithValue("$u", user);
                cmd.Parameters.AddWithValue("$a", amount);
                cmd.ExecuteNonQuery();
            }

            private bool IsProjectExistsInternal(SqliteConnection conn, SqliteTransaction tx, string projectId)
            {
                var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(1) FROM ProjectMembers WHERE ProjectId = $proj";
                cmd.Parameters.AddWithValue("$proj", projectId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }

            public bool IsProjectExists(string projectId)
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(1) FROM ProjectMembers WHERE ProjectId = $proj";
                cmd.Parameters.AddWithValue("$proj", projectId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }

            public bool BlockExists(string blockHash, string channelId = "System")
            {
                if (string.IsNullOrWhiteSpace(blockHash)) return false;

                string safeChannel = SanitizeChannelName(channelId);
                string tableName = safeChannel == "System" ? "Blocks" : $"Blocks_{safeChannel}";

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                using var cmdInit = connection.CreateCommand();
                cmdInit.CommandText = $"CREATE TABLE IF NOT EXISTS {tableName} (IndexId INTEGER PRIMARY KEY, Timestamp TEXT, Data TEXT, PreviousHash TEXT, Hash TEXT UNIQUE, ValidatorPublicKey TEXT, Signature TEXT, Nonce INTEGER)";
                cmdInit.ExecuteNonQuery();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(1) FROM {tableName} WHERE Hash = $hash";
                cmd.Parameters.AddWithValue("$hash", blockHash);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }

            public void SavePendingBlock(Block block, string reason)
            {
                if (string.IsNullOrWhiteSpace(block.Hash)) return;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT OR REPLACE INTO PendingBlocks
                (Hash, ChannelId, IndexId, Timestamp, Data, PreviousHash, ValidatorPublicKey, Signature, Nonce, Reason, ReceivedAt)
                VALUES ($hash, $channel, $idx, $time, $data, $prev, $val, $sig, $nonce, $reason, $received)";
                cmd.Parameters.AddWithValue("$hash", block.Hash);
                cmd.Parameters.AddWithValue("$channel", SanitizeChannelName(block.ChannelId));
                cmd.Parameters.AddWithValue("$idx", block.Index);
                cmd.Parameters.AddWithValue("$time", block.Timestamp.ToString("O"));
                cmd.Parameters.AddWithValue("$data", EncryptString(block.Data));
                cmd.Parameters.AddWithValue("$prev", block.PreviousHash);
                cmd.Parameters.AddWithValue("$val", EncryptString(block.ValidatorPublicKey ?? ""));
                cmd.Parameters.AddWithValue("$sig", EncryptString(block.Signature ?? ""));
                cmd.Parameters.AddWithValue("$nonce", block.Nonce);
                cmd.Parameters.AddWithValue("$reason", reason);
                cmd.Parameters.AddWithValue("$received", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            public List<Block> LoadPendingChildren(string previousHash, string channelId)
            {
                var blocks = new List<Block>();
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT IndexId, Timestamp, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce, ChannelId
                FROM PendingBlocks
                WHERE ChannelId = $channel AND PreviousHash = $prev
                ORDER BY IndexId ASC, ReceivedAt ASC";
                cmd.Parameters.AddWithValue("$channel", SanitizeChannelName(channelId));
                cmd.Parameters.AddWithValue("$prev", previousHash);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    blocks.Add(new Block
                    {
                        Index = Convert.ToInt32(reader.GetValue(0)),
                        Timestamp = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                        Data = DecryptString(reader.GetString(2)),
                        PreviousHash = reader.GetString(3),
                        Hash = reader.GetString(4),
                        ValidatorPublicKey = DecryptString(reader.IsDBNull(5) ? "" : reader.GetString(5)),
                        Signature = DecryptString(reader.IsDBNull(6) ? "" : reader.GetString(6)),
                        Nonce = Convert.ToInt64(reader.GetValue(7)),
                        ChannelId = reader.GetString(8)
                    });
                }

                return blocks;
            }

            public void RemovePendingBlock(string blockHash)
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM PendingBlocks WHERE Hash = $hash";
                cmd.Parameters.AddWithValue("$hash", blockHash);
                cmd.ExecuteNonQuery();
            }

            public void SavePeer(PeerInfo peer)
            {
                if (string.IsNullOrWhiteSpace(peer.Url)) return;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                EnsurePeerColumns(connection);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT INTO Peers (Url, NodeId, Role, LastSeen, IsTrusted)
                VALUES ($url, $nodeId, $role, $seen, $trusted)
                ON CONFLICT(Url) DO UPDATE SET
                    NodeId = CASE WHEN excluded.NodeId = '' THEN Peers.NodeId ELSE excluded.NodeId END,
                    Role = CASE WHEN excluded.Role = '' THEN Peers.Role ELSE excluded.Role END,
                    LastSeen = excluded.LastSeen,
                    IsTrusted = excluded.IsTrusted";
                cmd.Parameters.AddWithValue("$url", peer.Url);
                cmd.Parameters.AddWithValue("$nodeId", peer.NodeId ?? string.Empty);
                cmd.Parameters.AddWithValue("$role", string.IsNullOrWhiteSpace(peer.Role) ? "Full" : peer.Role);
                cmd.Parameters.AddWithValue("$seen", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$trusted", peer.IsTrusted ? 1 : 0);
                cmd.ExecuteNonQuery();
            }

            public void SavePeer(string url) => SavePeer(PeerInfo.FromUrl(url));

            public void MarkPeerSeen(string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                EnsurePeerColumns(connection);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT INTO Peers (Url, Role, LastSeen, IsTrusted)
                VALUES ($url, 'Full', $seen, 1)
                ON CONFLICT(Url) DO UPDATE SET LastSeen = excluded.LastSeen, IsTrusted = 1";
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$seen", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            public void MarkPeerFailure(string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                EnsurePeerColumns(connection);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT INTO Peers (Url, Role, LastFailure, IsTrusted)
                VALUES ($url, 'Full', $failure, 1)
                ON CONFLICT(Url) DO UPDATE SET LastFailure = excluded.LastFailure";
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$failure", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            public List<PeerInfo> LoadPeerInfos()
            {
                var peers = new List<PeerInfo>();
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                EnsurePeerColumns(connection);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Url, COALESCE(NodeId, ''), COALESCE(Role, 'Full'), LastSeen, LastFailure, IsTrusted FROM Peers WHERE IsTrusted = 1 ORDER BY Url";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    peers.Add(new PeerInfo(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        !reader.IsDBNull(5) && reader.GetInt32(5) != 0));
                }

                return peers;
            }

            public List<string> LoadPeers()
            {
                return LoadPeerInfos().Select(peer => peer.Url).ToList();
            }

            private static void EnsurePeerColumns(SqliteConnection connection)
            {
                EnsureColumn(connection, "Peers", "NodeId", "TEXT");
                EnsureColumn(connection, "Peers", "Role", "TEXT");
            }

            private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
            {
                using (var check = connection.CreateCommand())
                {
                    check.CommandText = $"PRAGMA table_info({tableName})";
                    using var reader = check.ExecuteReader();
                    while (reader.Read())
                    {
                        if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                }

                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
                alter.ExecuteNonQuery();
            }

            public List<string> GetKnownChannels()
            {
                var channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System" };

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT ProjectId FROM ProjectMembers WHERE ProjectId IS NOT NULL AND ProjectId <> ''";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string channel = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(channel))
                    {
                        channels.Add(SanitizeChannelName(channel));
                    }
                }

                using var tableCmd = connection.CreateCommand();
                tableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'Blocks_%'";
                using var tableReader = tableCmd.ExecuteReader();
                while (tableReader.Read())
                {
                    string tableName = tableReader.GetString(0);
                    string channel = tableName.Substring("Blocks_".Length);
                    if (!string.IsNullOrWhiteSpace(channel))
                    {
                        channels.Add(SanitizeChannelName(channel));
                    }
                }

                return channels.ToList();
            }

            public int GetUserBalance(string userName)
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Amount FROM Balances WHERE UserName = $user";
                cmd.Parameters.AddWithValue("$user", userName);
                var result = cmd.ExecuteScalar();
                return result != null ? Convert.ToInt32(result) : 0;
            }

            public string GetUserRole(string projectId, string userName)
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Role FROM ProjectMembers WHERE ProjectId = $proj AND UserName = $user";
                cmd.Parameters.AddWithValue("$proj", projectId);
                cmd.Parameters.AddWithValue("$user", userName);
                string role = DecryptString(cmd.ExecuteScalar()?.ToString());
                return string.IsNullOrWhiteSpace(role) ? "None" : role;
            }

            public List<Block> LoadChain(string channelId = "System")
            {
                var chain = new List<Block>();

                string safeChannel = SanitizeChannelName(channelId);

                string tableName = safeChannel == "System" ? "Blocks" : $"Blocks_{safeChannel}";

                try
                {
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
                    connection.Open();

                    using var cmdInit = connection.CreateCommand();
                    cmdInit.CommandText = $"CREATE TABLE IF NOT EXISTS {tableName} (IndexId INTEGER PRIMARY KEY, Timestamp TEXT, Data TEXT, PreviousHash TEXT, Hash TEXT, ValidatorPublicKey TEXT, Signature TEXT, Nonce INTEGER)";
                    cmdInit.ExecuteNonQuery();

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT IndexId, Timestamp, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce FROM {tableName} ORDER BY IndexId ASC";

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        try
                        {
                            chain.Add(ReadBlock(reader, safeChannel));
                        }
                        catch (Exception rowEx)
                        {
                            Console.WriteLine($"[CRITICAL] Error reading block from DB (Channel: {safeChannel}): {rowEx.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CRITICAL] Error accessing table {tableName}: {ex.Message}");
                }

                return chain;
            }

            public Block? GetLatestBlock(string channelId = "System")
            {
                string safeChannel = SanitizeChannelName(channelId);
                string tableName = safeChannel == "System" ? "Blocks" : $"Blocks_{safeChannel}";

                try
                {
                    using var connection = new SqliteConnection(ConnectionString);
                    connection.Open();

                    EnsureBlockTable(connection, tableName);

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT IndexId, Timestamp, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce FROM {tableName} ORDER BY IndexId DESC LIMIT 1";

                    using var reader = cmd.ExecuteReader();
                    return reader.Read() ? ReadBlock(reader, safeChannel) : null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CRITICAL] Error reading latest block from {tableName}: {ex.Message}");
                    return null;
                }
            }

            public List<Block> LoadLatestBlocks(string channelId = "System", int count = 100)
            {
                var blocks = new List<Block>();
                int limit = count > 0 ? count : 100;
                string safeChannel = SanitizeChannelName(channelId);
                string tableName = safeChannel == "System" ? "Blocks" : $"Blocks_{safeChannel}";

                try
                {
                    using var connection = new SqliteConnection(ConnectionString);
                    connection.Open();

                    EnsureBlockTable(connection, tableName);

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT IndexId, Timestamp, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce FROM {tableName} ORDER BY IndexId DESC LIMIT $limit";
                    cmd.Parameters.AddWithValue("$limit", limit);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        try
                        {
                            blocks.Add(ReadBlock(reader, safeChannel));
                        }
                        catch (Exception rowEx)
                        {
                            Console.WriteLine($"[CRITICAL] Error reading block from DB (Channel: {safeChannel}): {rowEx.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CRITICAL] Error accessing table {tableName}: {ex.Message}");
                }

                blocks.Reverse();
                return blocks;
            }

            public bool HasBlocks(string channelId = "System")
            {
                string safeChannel = SanitizeChannelName(channelId);
                string tableName = safeChannel == "System" ? "Blocks" : $"Blocks_{safeChannel}";

                try
                {
                    using var connection = new SqliteConnection(ConnectionString);
                    connection.Open();

                    EnsureBlockTable(connection, tableName);

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT 1 FROM {tableName} LIMIT 1";
                    return cmd.ExecuteScalar() != null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CRITICAL] Error checking table {tableName}: {ex.Message}");
                    return false;
                }
            }

            private void EnsureBlockTable(SqliteConnection connection, string tableName)
            {
                using var cmdInit = connection.CreateCommand();
                cmdInit.CommandText = $"CREATE TABLE IF NOT EXISTS {tableName} (IndexId INTEGER PRIMARY KEY, Timestamp TEXT, Data TEXT, PreviousHash TEXT, Hash TEXT, ValidatorPublicKey TEXT, Signature TEXT, Nonce INTEGER)";
                cmdInit.ExecuteNonQuery();
            }

            private Block ReadBlock(SqliteDataReader reader, string safeChannel)
            {
                return new Block
                {
                    Index = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0)),
                    Timestamp = reader.IsDBNull(1)
                        ? DateTime.MinValue
                        : (DateTime.TryParse(
                            reader.GetValue(1)?.ToString(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var ts)
                            ? ts
                            : DateTime.MinValue),
                    Data = DecryptString(reader.IsDBNull(2) ? "" : (reader.GetValue(2)?.ToString() ?? "")),
                    PreviousHash = reader.IsDBNull(3) ? "" : (reader.GetValue(3)?.ToString() ?? ""),
                    Hash = reader.IsDBNull(4) ? "" : (reader.GetValue(4)?.ToString() ?? ""),
                    ValidatorPublicKey = DecryptString(reader.IsDBNull(5) ? "" : (reader.GetValue(5)?.ToString() ?? "")),
                    Signature = DecryptString(reader.IsDBNull(6) ? "" : (reader.GetValue(6)?.ToString() ?? "")),
                    Nonce = reader.IsDBNull(7) ? 0 : Convert.ToInt64(reader.GetValue(7)),
                    ChannelId = safeChannel
                };
            }

            public void ReplaceChain(string channelId, List<Block> blocks)
            {
                string safeChannel = SanitizeChannelName(channelId);
                string tableName = safeChannel == "System" ? "Blocks" : $"Blocks_{safeChannel}";

                using (var connection = new SqliteConnection(ConnectionString))
                {
                    connection.Open();
                    using var transaction = connection.BeginTransaction();

                    if (safeChannel != "System")
                    {
                        CreateChannelTableInternal(connection, transaction, safeChannel);
                    }

                    using var deleteCmd = connection.CreateCommand();
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = $"DELETE FROM {tableName}";
                    deleteCmd.ExecuteNonQuery();

                    foreach (var block in blocks.OrderBy(b => b.Index))
                    {
                        InsertBlockInternal(connection, transaction, tableName, block);
                    }

                    transaction.Commit();
                }

                RebuildStateIndexes();
            }

            public void RebuildStateIndexes()
            {
                var channels = GetKnownChannels()
                    .OrderBy(channel => channel == "System" ? 0 : 1)
                    .ThenBy(channel => channel, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var chains = channels.ToDictionary(channel => channel, channel => LoadChain(channel), StringComparer.OrdinalIgnoreCase);

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                foreach (string table in new[] { "Tasks", "ProjectMembers", "Balances", "Users", "GovernanceVotes", "GovernanceProposals", "DocumentVersions" })
                {
                    using var clearCmd = connection.CreateCommand();
                    clearCmd.Transaction = transaction;
                    clearCmd.CommandText = $"DELETE FROM {table}";
                    clearCmd.ExecuteNonQuery();
                }

                foreach (var channel in channels)
                {
                    foreach (var block in chains[channel])
                    {
                        if (string.IsNullOrWhiteSpace(block.Data)) continue;
                        if (!block.Data.Trim().StartsWith("{") && !block.Data.Trim().StartsWith("[")) continue;

                        using var doc = JsonDocument.Parse(block.Data);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            UpdateStateIndex(connection, transaction, doc.RootElement, block.ValidatorPublicKey ?? "");
                        }
                    }
                }

                transaction.Commit();
            }

            private void InsertBlockInternal(SqliteConnection connection, SqliteTransaction transaction, string tableName, Block block)
            {
                var cmdBlock = connection.CreateCommand();
                cmdBlock.Transaction = transaction;

                cmdBlock.CommandText = $"INSERT OR IGNORE INTO {tableName} (IndexId, Timestamp, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce) VALUES ($idx, $time, $data, $prev, $hash, $val, $sig, $nonce)";
                cmdBlock.Parameters.AddWithValue("$idx", block.Index);
                cmdBlock.Parameters.AddWithValue("$time", block.Timestamp.ToString("O"));
                cmdBlock.Parameters.AddWithValue("$data", EncryptString(block.Data));
                cmdBlock.Parameters.AddWithValue("$prev", block.PreviousHash);
                cmdBlock.Parameters.AddWithValue("$hash", block.Hash);
                cmdBlock.Parameters.AddWithValue("$val", EncryptString(block.ValidatorPublicKey ?? ""));
                cmdBlock.Parameters.AddWithValue("$sig", EncryptString(block.Signature ?? ""));
                cmdBlock.Parameters.AddWithValue("$nonce", block.Nonce);
                cmdBlock.ExecuteNonQuery();
            }

            public void SaveToMempool(string txId, string json)
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();

                command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Mempool (
                    TxId TEXT PRIMARY KEY,
                    PayloadJson TEXT NOT NULL,
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                INSERT OR IGNORE INTO Mempool (TxId, PayloadJson) VALUES (@txId, @data);";

                command.Parameters.AddWithValue("@txId", txId);
                command.Parameters.AddWithValue("@data", EncryptString(json));
                command.ExecuteNonQuery();
            }

            public List<string> GetMempoolTransactions(int limit)
            {
                var result = new List<string>();
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                // CHANGED: schema self-healing for old "Data" column in mempool table.
                using (var cmdCheck = connection.CreateCommand())
                {
                    cmdCheck.CommandText = "PRAGMA table_info(Mempool)";
                    using var schemaReader = cmdCheck.ExecuteReader();
                    bool hasPayloadJson = false;
                    bool hasData = false;
                    while (schemaReader.Read())
                    {
                        string column = schemaReader.GetString(1);
                        if (string.Equals(column, "PayloadJson", StringComparison.OrdinalIgnoreCase)) hasPayloadJson = true;
                        if (string.Equals(column, "Data", StringComparison.OrdinalIgnoreCase)) hasData = true;
                    }

                    if (!hasPayloadJson && hasData)
                    {
                        using var migrate = connection.CreateCommand();
                        migrate.CommandText = @"
                        ALTER TABLE Mempool RENAME TO Mempool_legacy;
                        CREATE TABLE Mempool (
                            TxId TEXT PRIMARY KEY,
                            PayloadJson TEXT NOT NULL,
                            Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
                        );
                        INSERT OR IGNORE INTO Mempool (TxId, PayloadJson, Timestamp)
                        SELECT TxId, Data, Timestamp FROM Mempool_legacy;
                        DROP TABLE Mempool_legacy;";
                        migrate.ExecuteNonQuery();
                    }
                }
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT PayloadJson FROM Mempool ORDER BY Timestamp ASC LIMIT $limit";
                cmd.Parameters.AddWithValue("$limit", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add(DecryptString(reader.GetString(0)));
                return result;
            }

            public List<string> GetUserProjects(string userName)
            {
                var list = new List<string>();
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
                connection.Open();
                try
                {
                    var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT DISTINCT ProjectId FROM ProjectMembers WHERE UserName = $u";
                    cmd.Parameters.AddWithValue("$u", userName);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) list.Add(reader.GetString(0));
                }
                catch { }
                return list;
            }

            public string? GetUserPublicKey(string userName)
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT PublicKey FROM Users WHERE UserName = $user";
                cmd.Parameters.AddWithValue("$user", userName);
                return DecryptString(cmd.ExecuteScalar()?.ToString());
            }

            public void ClearMempool()
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM Mempool";
                cmd.ExecuteNonQuery();
            }
        }
    }

