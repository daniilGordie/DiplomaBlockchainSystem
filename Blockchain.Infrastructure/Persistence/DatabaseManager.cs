using Microsoft.Data.Sqlite;
using Blockchain.Core;
using Blockchain.Core.Consensus;
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
            private readonly byte[]? _legacyFieldEncryptionKey;
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
                if (!string.IsNullOrWhiteSpace(_dbPassword))
                {
                    _fieldEncryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes($"{_dbPassword}|nexus-field-encryption"));
                    _legacyFieldEncryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes($"{_dbPassword}|nexus-field-encryption|{DbFileName}"));
                }
                try
                {
                    InitializeDatabase();
                }
                catch (SqliteException ex) when (IsDatabaseOpenFailure(ex))
                {
                    throw new InvalidOperationException(
                        $"Node database '{DbFileName}' cannot be opened. The file is not a valid Nexus SQLite database for the configured NodeDbPassword. " +
                        "If this is a new node or disposable Docker smoke environment, recreate the node data volume. If this is an existing node, restore the original NodeDbPassword or database backup.",
                        ex);
                }
            }

            private bool IsFieldEncryptionEnabled => _fieldEncryptionKey is { Length: > 0 };

            private static bool IsDatabaseOpenFailure(SqliteException ex)
            {
                return ex.SqliteErrorCode is 26 or 14
                    || ex.Message.Contains("file is not a database", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase);
            }

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

                    if (TryDecryptString(payload, _fieldEncryptionKey, out string plaintext))
                    {
                        return plaintext;
                    }

                    if (_legacyFieldEncryptionKey is not null &&
                        !CryptographicOperations.FixedTimeEquals(_fieldEncryptionKey!, _legacyFieldEncryptionKey) &&
                        TryDecryptString(payload, _legacyFieldEncryptionKey, out plaintext))
                    {
                        return plaintext;
                    }

                    return string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }

            private static bool TryDecryptString(byte[] payload, byte[]? key, out string value)
            {
                value = string.Empty;
                if (key is not { Length: > 0 })
                {
                    return false;
                }

                try
                {
                    byte[] nonce = payload[..12];
                    byte[] tag = payload[12..28];
                    byte[] ciphertext = payload[28..];
                    byte[] plaintext = new byte[ciphertext.Length];

                    using var aes = new AesGcm(key, EncryptionTagSize);
                    aes.Decrypt(nonce, ciphertext, tag, plaintext);
                    value = Encoding.UTF8.GetString(plaintext);
                    return true;
                }
                catch
                {
                    return false;
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
                        IndexId INTEGER PRIMARY KEY, Timestamp TEXT, TimestampUnixSeconds INTEGER NOT NULL DEFAULT 0, Data TEXT, PreviousHash TEXT, 
                        Hash TEXT UNIQUE, ValidatorPublicKey TEXT, Signature TEXT, Nonce INTEGER
                    );
                    CREATE INDEX IF NOT EXISTS idx_hash ON Blocks(Hash);";
                    cmd.ExecuteNonQuery();
                    EnsureBlockColumns(connection, "Blocks");

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
                        TimestampUnixSeconds INTEGER NOT NULL DEFAULT 0,
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
                    EnsureColumn(connection, "PendingBlocks", "TimestampUnixSeconds", "INTEGER NOT NULL DEFAULT 0");

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS BlockFinalityMetadata (
                        BlockHash TEXT PRIMARY KEY,
                        ChannelId TEXT NOT NULL,
                        FinalityMode TEXT NOT NULL,
                        RaftLogIndex INTEGER,
                        RaftTerm INTEGER,
                        CommittedAtUtc TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_finality_channel ON BlockFinalityMetadata(ChannelId);";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS BlockContributionProofs (
                        BlockHash TEXT PRIMARY KEY,
                        ProjectId TEXT NOT NULL,
                        Epoch INTEGER NOT NULL,
                        ProducerPublicKey TEXT NOT NULL,
                        ProducerScore INTEGER NOT NULL,
                        ScoreSnapshotHash TEXT NOT NULL,
                        EvidenceBlockHashesJson TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_contribution_proofs_project ON BlockContributionProofs(ProjectId, Epoch);";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS IntentOutbox (
                        IntentId TEXT PRIMARY KEY,
                        NetworkId TEXT NOT NULL,
                        ProjectId TEXT NOT NULL,
                        ChannelId TEXT NOT NULL,
                        OperationType TEXT NOT NULL,
                        CorrelationId TEXT,
                        IntentJson TEXT NOT NULL,
                        BlockEnvelopeJson TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        AttemptCount INTEGER NOT NULL DEFAULT 0,
                        CreatedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL,
                        LastAttemptAtUtc TEXT,
                        NextAttemptAtUtc TEXT,
                        LastError TEXT,
                        Destination TEXT,
                        CommittedBlockHash TEXT,
                        CommittedBlockIndex INTEGER,
                        ProposalId TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_intent_outbox_status_next ON IntentOutbox(Status, NextAttemptAtUtc);
                    CREATE INDEX IF NOT EXISTS idx_intent_outbox_channel ON IntentOutbox(ChannelId, UpdatedAtUtc);";
                    cmd.ExecuteNonQuery();
                    EnsureIntentOutboxColumns(connection);

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS IntentStatusHistory (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        IntentId TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        ChangedAtUtc TEXT NOT NULL,
                        Message TEXT,
                        Destination TEXT,
                        ProposalId TEXT,
                        CommittedBlockHash TEXT,
                        CommittedBlockIndex INTEGER
                    );
                    CREATE INDEX IF NOT EXISTS idx_intent_history_intent ON IntentStatusHistory(IntentId, ChangedAtUtc);";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Peers (
                        Url TEXT PRIMARY KEY,
                        NodeId TEXT,
                        Role TEXT,
                        LastSeen TEXT,
                        LastFailure TEXT,
                        IsTrusted INTEGER NOT NULL DEFAULT 1,
                        NodePublicKey TEXT,
                        NetworkId TEXT,
                        PublicKeyFingerprint TEXT,
                        IrohNodeId TEXT,
                        RequestedRole TEXT,
                        MembershipStatus TEXT,
                        ApprovedAt TEXT,
                        ApprovedBy TEXT,
                        RejectedAt TEXT,
                        RejectedBy TEXT,
                        RevokedAt TEXT,
                        RevokedBy TEXT,
                        Reason TEXT,
                        AppVersion TEXT,
                        ProtocolVersion TEXT,
                        Capabilities TEXT
                    );";
                    cmd.ExecuteNonQuery();
                    EnsurePeerColumns(connection);

                    cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS PeerAuditLog (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Url TEXT NOT NULL,
                        Actor TEXT,
                        Action TEXT NOT NULL,
                        OldValue TEXT,
                        NewValue TEXT,
                        Reason TEXT,
                        CorrelationId TEXT,
                        TimestampUtc TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_peer_audit_url ON PeerAuditLog(Url, TimestampUtc);";
                    cmd.ExecuteNonQuery();
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
                    IndexId INTEGER PRIMARY KEY, Timestamp TEXT, TimestampUnixSeconds INTEGER NOT NULL DEFAULT 0, Data TEXT, PreviousHash TEXT, 
                    Hash TEXT UNIQUE, ValidatorPublicKey TEXT, Signature TEXT, Nonce INTEGER
                );
                CREATE INDEX IF NOT EXISTS idx_hash_{safeChannel} ON Blocks_{safeChannel}(Hash);";
                cmd.ExecuteNonQuery();
                EnsureBlockColumns(conn, tx, $"Blocks_{safeChannel}");
            }

            public static void EnsureInitialized(string dbName = "nexus_node.db")
            {
                new DatabaseManager(dbName);
            }

            public void SaveIntent(IntentOutboxRecord record)
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Intent.IntentId)) return;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT INTO IntentOutbox
                (IntentId, NetworkId, ProjectId, ChannelId, OperationType, CorrelationId, IntentJson, BlockEnvelopeJson, Status, AttemptCount, CreatedAtUtc, UpdatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, LastError, Destination, CommittedBlockHash, CommittedBlockIndex, ProposalId)
                VALUES ($intentId, $networkId, $projectId, $channelId, $operationType, $correlationId, $intentJson, $blockJson, $status, $attempts, $created, $updated, $lastAttempt, $nextAttempt, $lastError, $destination, $committedHash, $committedIndex, $proposalId)
                ON CONFLICT(IntentId) DO UPDATE SET
                    IntentJson = excluded.IntentJson,
                    BlockEnvelopeJson = excluded.BlockEnvelopeJson,
                    Status = excluded.Status,
                    AttemptCount = excluded.AttemptCount,
                    UpdatedAtUtc = excluded.UpdatedAtUtc,
                    LastAttemptAtUtc = excluded.LastAttemptAtUtc,
                    NextAttemptAtUtc = excluded.NextAttemptAtUtc,
                    LastError = excluded.LastError,
                    Destination = excluded.Destination,
                    CommittedBlockHash = excluded.CommittedBlockHash,
                    CommittedBlockIndex = excluded.CommittedBlockIndex,
                    ProposalId = excluded.ProposalId";
                cmd.Parameters.AddWithValue("$intentId", record.Intent.IntentId);
                cmd.Parameters.AddWithValue("$networkId", record.Intent.NetworkId);
                cmd.Parameters.AddWithValue("$projectId", record.Intent.ProjectId);
                cmd.Parameters.AddWithValue("$channelId", ChannelName.Normalize(record.Intent.ChannelId));
                cmd.Parameters.AddWithValue("$operationType", record.Intent.OperationType);
                cmd.Parameters.AddWithValue("$correlationId", record.Intent.CorrelationId ?? string.Empty);
                cmd.Parameters.AddWithValue("$intentJson", EncryptString(JsonSerializer.Serialize(record.Intent)));
                cmd.Parameters.AddWithValue("$blockJson", EncryptString(record.BlockEnvelopeJson ?? string.Empty));
                cmd.Parameters.AddWithValue("$status", record.Status.ToString());
                cmd.Parameters.AddWithValue("$attempts", record.AttemptCount);
                cmd.Parameters.AddWithValue("$created", record.CreatedAtUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$updated", record.UpdatedAtUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$lastAttempt", record.LastAttemptAtUtc?.ToString("O") ?? string.Empty);
                cmd.Parameters.AddWithValue("$nextAttempt", record.NextAttemptAtUtc?.ToString("O") ?? string.Empty);
                cmd.Parameters.AddWithValue("$lastError", EncryptString(record.LastError ?? string.Empty));
                cmd.Parameters.AddWithValue("$destination", record.Destination ?? string.Empty);
                cmd.Parameters.AddWithValue("$committedHash", record.CommittedBlockHash ?? string.Empty);
                cmd.Parameters.AddWithValue("$committedIndex", record.CommittedBlockIndex.HasValue ? record.CommittedBlockIndex.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$proposalId", record.ProposalId ?? string.Empty);
                cmd.ExecuteNonQuery();
                AppendIntentHistory(connection, record.Intent.IntentId, record.Status, record.UpdatedAtUtc, record.LastError ?? string.Empty, record.Destination ?? string.Empty, record.ProposalId, record.CommittedBlockHash, record.CommittedBlockIndex);
            }

            public IntentOutboxRecord? GetIntent(string intentId)
            {
                if (string.IsNullOrWhiteSpace(intentId)) return null;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT IntentJson, BlockEnvelopeJson, Status, AttemptCount, CreatedAtUtc, UpdatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, LastError, Destination, CommittedBlockHash, CommittedBlockIndex, ProposalId
                FROM IntentOutbox
                WHERE IntentId = $intentId
                LIMIT 1";
                cmd.Parameters.AddWithValue("$intentId", intentId);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadIntentOutboxRecord(reader) : null;
            }

            public List<IntentOutboxRecord> LoadRetryableIntents(DateTime nowUtc, int limit)
            {
                var records = new List<IntentOutboxRecord>();
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT IntentJson, BlockEnvelopeJson, Status, AttemptCount, CreatedAtUtc, UpdatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, LastError, Destination, CommittedBlockHash, CommittedBlockIndex, ProposalId
                FROM IntentOutbox
                WHERE Status IN ('Created', 'QueuedOffline', 'Submitted', 'Accepted', 'FailedRetryable')
                  AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc = '' OR NextAttemptAtUtc <= $now)
                ORDER BY UpdatedAtUtc ASC
                LIMIT $limit";
                cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var record = ReadIntentOutboxRecord(reader);
                    if (record != null)
                    {
                        records.Add(record);
                    }
                }

                return records;
            }

            public List<IntentOutboxRecord> LoadRecentIntents(int limit)
            {
                var records = new List<IntentOutboxRecord>();
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT IntentJson, BlockEnvelopeJson, Status, AttemptCount, CreatedAtUtc, UpdatedAtUtc, LastAttemptAtUtc, NextAttemptAtUtc, LastError, Destination, CommittedBlockHash, CommittedBlockIndex, ProposalId
                FROM IntentOutbox
                ORDER BY UpdatedAtUtc DESC
                LIMIT $limit";
                cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var record = ReadIntentOutboxRecord(reader);
                    if (record != null)
                    {
                        records.Add(record);
                    }
                }

                return records;
            }

            public void UpdateIntentStatus(
                string intentId,
                IntentStatus status,
                int attemptCount,
                DateTime updatedAtUtc,
                DateTime? lastAttemptAtUtc,
                DateTime? nextAttemptAtUtc,
                string lastError,
                string destination,
                string? committedBlockHash,
                long? committedBlockIndex = null,
                string? proposalId = null)
            {
                if (string.IsNullOrWhiteSpace(intentId)) return;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                UPDATE IntentOutbox
                SET Status = $status,
                    AttemptCount = $attempts,
                    UpdatedAtUtc = $updated,
                    LastAttemptAtUtc = $lastAttempt,
                    NextAttemptAtUtc = $nextAttempt,
                    LastError = $lastError,
                    Destination = $destination,
                    CommittedBlockHash = $committedHash,
                    CommittedBlockIndex = $committedIndex,
                    ProposalId = $proposalId
                WHERE IntentId = $intentId";
                cmd.Parameters.AddWithValue("$intentId", intentId);
                cmd.Parameters.AddWithValue("$status", status.ToString());
                cmd.Parameters.AddWithValue("$attempts", attemptCount);
                cmd.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$lastAttempt", lastAttemptAtUtc?.ToString("O") ?? string.Empty);
                cmd.Parameters.AddWithValue("$nextAttempt", nextAttemptAtUtc?.ToString("O") ?? string.Empty);
                cmd.Parameters.AddWithValue("$lastError", EncryptString(lastError ?? string.Empty));
                cmd.Parameters.AddWithValue("$destination", destination ?? string.Empty);
                cmd.Parameters.AddWithValue("$committedHash", committedBlockHash ?? string.Empty);
                cmd.Parameters.AddWithValue("$committedIndex", committedBlockIndex.HasValue ? committedBlockIndex.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$proposalId", proposalId ?? string.Empty);
                cmd.ExecuteNonQuery();
                AppendIntentHistory(connection, intentId, status, updatedAtUtc, lastError ?? string.Empty, destination ?? string.Empty, proposalId, committedBlockHash, committedBlockIndex);
            }

            public List<IntentStatusTransition> LoadIntentHistory(string intentId)
            {
                var history = new List<IntentStatusTransition>();
                if (string.IsNullOrWhiteSpace(intentId)) return history;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT IntentId, Status, ChangedAtUtc, Message, Destination, ProposalId, CommittedBlockHash, CommittedBlockIndex
                FROM IntentStatusHistory
                WHERE IntentId = $intentId
                ORDER BY ChangedAtUtc ASC, Id ASC";
                cmd.Parameters.AddWithValue("$intentId", intentId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var status = Enum.TryParse<IntentStatus>(reader.GetString(1), true, out var parsed)
                        ? parsed
                        : IntentStatus.FailedRetryable;
                    history.Add(new IntentStatusTransition(
                        reader.GetString(0),
                        status,
                        ParseUtc(reader.GetString(2)) ?? DateTime.UtcNow,
                        DecryptString(reader.IsDBNull(3) ? string.Empty : reader.GetString(3)),
                        reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                        reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetInt64(7)));
                }

                return history;
            }

            private IntentOutboxRecord? ReadIntentOutboxRecord(SqliteDataReader reader)
            {
                try
                {
                    string intentJson = DecryptString(reader.GetString(0));
                    var intent = JsonSerializer.Deserialize<SignedIntent>(intentJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (intent == null)
                    {
                        return null;
                    }

                    string statusRaw = reader.GetString(2);
                    var status = Enum.TryParse<IntentStatus>(statusRaw, true, out var parsedStatus)
                        ? parsedStatus
                        : IntentStatus.FailedRetryable;
                    return new IntentOutboxRecord(
                        intent,
                        DecryptString(reader.GetString(1)),
                        status,
                        Convert.ToInt32(reader.GetValue(3)),
                        ParseUtc(reader.GetString(4)) ?? DateTime.UtcNow,
                        ParseUtc(reader.GetString(5)) ?? DateTime.UtcNow,
                        ParseUtc(reader.IsDBNull(6) ? string.Empty : reader.GetString(6)),
                        ParseUtc(reader.IsDBNull(7) ? string.Empty : reader.GetString(7)),
                        DecryptString(reader.IsDBNull(8) ? string.Empty : reader.GetString(8)),
                        reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                        reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                        reader.IsDBNull(11) ? null : reader.GetInt64(11),
                        reader.IsDBNull(12) ? string.Empty : reader.GetString(12));
                }
                catch
                {
                    return null;
                }
            }

            private void AppendIntentHistory(
                SqliteConnection connection,
                string intentId,
                IntentStatus status,
                DateTime changedAtUtc,
                string message,
                string destination,
                string? proposalId,
                string? committedBlockHash,
                long? committedBlockIndex)
            {
                if (string.IsNullOrWhiteSpace(intentId)) return;

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT INTO IntentStatusHistory
                (IntentId, Status, ChangedAtUtc, Message, Destination, ProposalId, CommittedBlockHash, CommittedBlockIndex)
                VALUES ($intentId, $status, $changedAt, $message, $destination, $proposalId, $committedHash, $committedIndex)";
                cmd.Parameters.AddWithValue("$intentId", intentId);
                cmd.Parameters.AddWithValue("$status", status.ToString());
                cmd.Parameters.AddWithValue("$changedAt", changedAtUtc.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$message", EncryptString(message ?? string.Empty));
                cmd.Parameters.AddWithValue("$destination", destination ?? string.Empty);
                cmd.Parameters.AddWithValue("$proposalId", proposalId ?? string.Empty);
                cmd.Parameters.AddWithValue("$committedHash", committedBlockHash ?? string.Empty);
                cmd.Parameters.AddWithValue("$committedIndex", committedBlockIndex.HasValue ? committedBlockIndex.Value : DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            private static DateTime? ParseUtc(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed.ToUniversalTime()
                    : null;
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

                            EnsureBlockColumns(connection, transaction, tableName);

                            cmdBlock.CommandText = $"INSERT INTO {tableName} (IndexId, Timestamp, TimestampUnixSeconds, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce) VALUES ($idx, $time, $timeUnix, $data, $prev, $hash, $val, $sig, $nonce)";
                            cmdBlock.Parameters.AddWithValue("$idx", block.Index);
                            cmdBlock.Parameters.AddWithValue("$time", block.Timestamp.ToString("O"));
                            cmdBlock.Parameters.AddWithValue("$timeUnix", block.TimestampUnixSeconds);
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
                    string projRole = ChannelName.Normalize(GetStringSafe(root, "ProjectId", "System"));

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
                    string proj = ChannelName.Normalize(GetStringSafe(root, "ProjectId", "System"));
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
                string safeProjectId = ChannelName.Normalize(projectId);
                cmd.Parameters.AddWithValue("$proj", safeProjectId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }

            public bool IsProjectExists(string projectId)
            {
                string safeProjectId = ChannelName.Normalize(projectId);
                string rawProjectId = projectId ?? string.Empty;
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(1) FROM ProjectMembers WHERE ProjectId = $proj OR ProjectId = $raw";
                cmd.Parameters.AddWithValue("$proj", safeProjectId);
                cmd.Parameters.AddWithValue("$raw", rawProjectId);
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
                (Hash, ChannelId, IndexId, Timestamp, TimestampUnixSeconds, Data, PreviousHash, ValidatorPublicKey, Signature, Nonce, Reason, ReceivedAt)
                VALUES ($hash, $channel, $idx, $time, $timeUnix, $data, $prev, $val, $sig, $nonce, $reason, $received)";
                cmd.Parameters.AddWithValue("$hash", block.Hash);
                cmd.Parameters.AddWithValue("$channel", SanitizeChannelName(block.ChannelId));
                cmd.Parameters.AddWithValue("$idx", block.Index);
                cmd.Parameters.AddWithValue("$time", block.Timestamp.ToString("O"));
                cmd.Parameters.AddWithValue("$timeUnix", block.TimestampUnixSeconds);
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
                SELECT IndexId, Timestamp, TimestampUnixSeconds, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce, ChannelId
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
                        TimestampUnixSeconds = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2)),
                        Data = DecryptString(reader.GetString(3)),
                        PreviousHash = reader.GetString(4),
                        Hash = reader.GetString(5),
                        ValidatorPublicKey = DecryptString(reader.IsDBNull(6) ? "" : reader.GetString(6)),
                        Signature = DecryptString(reader.IsDBNull(7) ? "" : reader.GetString(7)),
                        Nonce = Convert.ToInt64(reader.GetValue(8)),
                        ChannelId = reader.GetString(9)
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
                INSERT INTO Peers (Url, NodeId, Role, LastSeen, IsTrusted, NodePublicKey, NetworkId, PublicKeyFingerprint, IrohNodeId, RequestedRole, MembershipStatus, AppVersion, ProtocolVersion, Capabilities)
                VALUES ($url, $nodeId, $role, $seen, $trusted, $nodePublicKey, $networkId, $fingerprint, $irohNodeId, $requestedRole, $membershipStatus, $appVersion, $protocolVersion, $capabilities)
                ON CONFLICT(Url) DO UPDATE SET
                    NodeId = CASE WHEN excluded.NodeId = '' THEN Peers.NodeId ELSE excluded.NodeId END,
                    Role = CASE WHEN excluded.Role = '' THEN Peers.Role ELSE excluded.Role END,
                    NodePublicKey = CASE WHEN excluded.NodePublicKey = '' THEN Peers.NodePublicKey ELSE excluded.NodePublicKey END,
                    NetworkId = CASE WHEN excluded.NetworkId = '' THEN Peers.NetworkId ELSE excluded.NetworkId END,
                    PublicKeyFingerprint = CASE WHEN excluded.PublicKeyFingerprint = '' THEN Peers.PublicKeyFingerprint ELSE excluded.PublicKeyFingerprint END,
                    IrohNodeId = CASE WHEN excluded.IrohNodeId = '' THEN Peers.IrohNodeId ELSE excluded.IrohNodeId END,
                    RequestedRole = CASE WHEN excluded.RequestedRole = '' THEN Peers.RequestedRole ELSE excluded.RequestedRole END,
                    MembershipStatus = CASE WHEN excluded.MembershipStatus = '' THEN Peers.MembershipStatus ELSE excluded.MembershipStatus END,
                    AppVersion = CASE WHEN excluded.AppVersion = '' THEN Peers.AppVersion ELSE excluded.AppVersion END,
                    ProtocolVersion = CASE WHEN excluded.ProtocolVersion = '' THEN Peers.ProtocolVersion ELSE excluded.ProtocolVersion END,
                    Capabilities = CASE WHEN excluded.Capabilities = '' THEN Peers.Capabilities ELSE excluded.Capabilities END,
                    LastSeen = excluded.LastSeen,
                    IsTrusted = excluded.IsTrusted";
                cmd.Parameters.AddWithValue("$url", peer.Url);
                cmd.Parameters.AddWithValue("$nodeId", peer.NodeId ?? string.Empty);
                cmd.Parameters.AddWithValue("$role", string.IsNullOrWhiteSpace(peer.Role) ? "Full" : peer.Role);
                cmd.Parameters.AddWithValue("$seen", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$trusted", peer.IsTrusted ? 1 : 0);
                cmd.Parameters.AddWithValue("$nodePublicKey", peer.NodePublicKey ?? string.Empty);
                cmd.Parameters.AddWithValue("$networkId", peer.NetworkId ?? string.Empty);
                cmd.Parameters.AddWithValue("$fingerprint", peer.PublicKeyFingerprint ?? Fingerprint(peer.NodePublicKey));
                cmd.Parameters.AddWithValue("$irohNodeId", peer.IrohNodeId ?? string.Empty);
                cmd.Parameters.AddWithValue("$requestedRole", peer.RequestedRole ?? peer.Role ?? string.Empty);
                cmd.Parameters.AddWithValue("$membershipStatus", string.IsNullOrWhiteSpace(peer.MembershipStatus)
                    ? (peer.IsTrusted ? PeerMembershipStatuses.Approved : PeerMembershipStatuses.PendingApproval)
                    : peer.MembershipStatus);
                cmd.Parameters.AddWithValue("$appVersion", peer.AppVersion ?? string.Empty);
                cmd.Parameters.AddWithValue("$protocolVersion", peer.ProtocolVersion ?? string.Empty);
                cmd.Parameters.AddWithValue("$capabilities", peer.Capabilities ?? string.Empty);
                cmd.ExecuteNonQuery();
            }

            public void SavePeer(string url) => SavePeer(PeerInfo.FromUrl(url));

            public void SetPeerTrust(string url, bool isTrusted)
            {
                if (string.IsNullOrWhiteSpace(url)) return;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                EnsurePeerColumns(connection);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT INTO Peers (Url, Role, LastSeen, IsTrusted)
                VALUES ($url, 'Full', $seen, $trusted)
                ON CONFLICT(Url) DO UPDATE SET
                    IsTrusted = excluded.IsTrusted,
                    MembershipStatus = CASE WHEN excluded.IsTrusted = 1 THEN 'Approved' ELSE 'Revoked' END,
                    ApprovedAt = CASE WHEN excluded.IsTrusted = 1 THEN excluded.LastSeen ELSE Peers.ApprovedAt END,
                    RevokedAt = CASE WHEN excluded.IsTrusted = 0 THEN excluded.LastSeen ELSE Peers.RevokedAt END";
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$seen", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$trusted", isTrusted ? 1 : 0);
                cmd.ExecuteNonQuery();
                AppendPeerAudit(connection, url, "system", isTrusted ? "Approve" : "Revoke", "", isTrusted ? PeerMembershipStatuses.Approved : PeerMembershipStatuses.Revoked, "", Guid.NewGuid().ToString("N"));
            }

            public void SetPeerRole(string url, string role)
            {
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(role)) return;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                EnsurePeerColumns(connection);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT INTO Peers (Url, Role, LastSeen, IsTrusted)
                VALUES ($url, $role, $seen, 0)
                ON CONFLICT(Url) DO UPDATE SET Role = excluded.Role";
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$role", role.Trim());
                cmd.Parameters.AddWithValue("$seen", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
                AppendPeerAudit(connection, url, "system", "SetRole", "", role.Trim(), "", Guid.NewGuid().ToString("N"));
            }

            public void SetPeerMembership(string url, string status, string actor, string reason)
            {
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(status)) return;

                string normalized = NormalizeMembershipStatus(status);
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                EnsurePeerColumns(connection);
                string oldValue = LoadPeerStatus(connection, url);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT INTO Peers (Url, Role, LastSeen, IsTrusted, MembershipStatus, Reason)
                VALUES ($url, 'Edge', $now, $trusted, $status, $reason)
                ON CONFLICT(Url) DO UPDATE SET
                    IsTrusted = $trusted,
                    MembershipStatus = $status,
                    Reason = $reason,
                    ApprovedAt = CASE WHEN $status = 'Approved' THEN $now ELSE Peers.ApprovedAt END,
                    ApprovedBy = CASE WHEN $status = 'Approved' THEN $actor ELSE Peers.ApprovedBy END,
                    RejectedAt = CASE WHEN $status = 'Rejected' THEN $now ELSE Peers.RejectedAt END,
                    RejectedBy = CASE WHEN $status = 'Rejected' THEN $actor ELSE Peers.RejectedBy END,
                    RevokedAt = CASE WHEN $status = 'Revoked' THEN $now ELSE Peers.RevokedAt END,
                    RevokedBy = CASE WHEN $status = 'Revoked' THEN $actor ELSE Peers.RevokedBy END";
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$trusted", normalized == PeerMembershipStatuses.Approved || normalized == PeerMembershipStatuses.ConsensusCandidate ? 1 : 0);
                cmd.Parameters.AddWithValue("$status", normalized);
                cmd.Parameters.AddWithValue("$actor", actor ?? string.Empty);
                cmd.Parameters.AddWithValue("$reason", reason ?? string.Empty);
                cmd.ExecuteNonQuery();
                AppendPeerAudit(connection, url, actor, "SetMembership", oldValue, normalized, reason, Guid.NewGuid().ToString("N"));
            }

            public void SetPeerCapabilities(string url, string capabilities, string actor, string reason)
            {
                if (string.IsNullOrWhiteSpace(url)) return;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                EnsurePeerColumns(connection);
                string oldValue = LoadPeerCapabilities(connection, url);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT INTO Peers (Url, Role, LastSeen, IsTrusted, Capabilities)
                VALUES ($url, 'Edge', $now, 0, $capabilities)
                ON CONFLICT(Url) DO UPDATE SET Capabilities = excluded.Capabilities";
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$capabilities", capabilities ?? string.Empty);
                cmd.ExecuteNonQuery();
                AppendPeerAudit(connection, url, actor, "SetCapabilities", oldValue, capabilities ?? string.Empty, reason, Guid.NewGuid().ToString("N"));
            }

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

            public List<PeerInfo> LoadAllPeerInfos()
            {
                var peers = new List<PeerInfo>();
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                EnsurePeerColumns(connection);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT Url, COALESCE(NodeId, ''), COALESCE(Role, 'Full'), LastSeen, LastFailure, IsTrusted, COALESCE(NodePublicKey, ''),
                       COALESCE(NetworkId, ''), COALESCE(PublicKeyFingerprint, ''), COALESCE(IrohNodeId, ''), COALESCE(RequestedRole, ''),
                       COALESCE(MembershipStatus, CASE WHEN IsTrusted = 1 THEN 'Approved' ELSE 'PendingApproval' END),
                       COALESCE(ApprovedAt, ''), COALESCE(ApprovedBy, ''), COALESCE(RejectedAt, ''), COALESCE(RejectedBy, ''),
                       COALESCE(RevokedAt, ''), COALESCE(RevokedBy, ''), COALESCE(Reason, ''), COALESCE(AppVersion, ''),
                       COALESCE(ProtocolVersion, ''), COALESCE(Capabilities, '')
                FROM Peers
                ORDER BY Url";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    peers.Add(new PeerInfo(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        !reader.IsDBNull(5) && reader.GetInt32(5) != 0,
                        reader.GetString(6),
                        reader.GetString(7),
                        string.IsNullOrWhiteSpace(reader.GetString(8)) ? Fingerprint(reader.GetString(6)) : reader.GetString(8),
                        reader.GetString(9),
                        reader.GetString(10),
                        reader.GetString(11),
                        reader.GetString(12),
                        reader.GetString(13),
                        reader.GetString(14),
                        reader.GetString(15),
                        reader.GetString(16),
                        reader.GetString(17),
                        reader.GetString(18),
                        reader.GetString(19),
                        reader.GetString(20),
                        reader.GetString(21)));
                }

                return peers;
            }

            public List<PeerInfo> LoadPeerInfos()
            {
                return LoadAllPeerInfos()
                    .Where(peer => peer.IsTrusted)
                    .ToList();
            }

            public List<string> LoadPeers()
            {
                return LoadPeerInfos().Select(peer => peer.Url).ToList();
            }

            private static void EnsurePeerColumns(SqliteConnection connection)
            {
                EnsureColumn(connection, "Peers", "NodeId", "TEXT");
                EnsureColumn(connection, "Peers", "Role", "TEXT");
                EnsureColumn(connection, "Peers", "NodePublicKey", "TEXT");
                EnsureColumn(connection, "Peers", "NetworkId", "TEXT");
                EnsureColumn(connection, "Peers", "PublicKeyFingerprint", "TEXT");
                EnsureColumn(connection, "Peers", "IrohNodeId", "TEXT");
                EnsureColumn(connection, "Peers", "RequestedRole", "TEXT");
                EnsureColumn(connection, "Peers", "MembershipStatus", "TEXT");
                EnsureColumn(connection, "Peers", "ApprovedAt", "TEXT");
                EnsureColumn(connection, "Peers", "ApprovedBy", "TEXT");
                EnsureColumn(connection, "Peers", "RejectedAt", "TEXT");
                EnsureColumn(connection, "Peers", "RejectedBy", "TEXT");
                EnsureColumn(connection, "Peers", "RevokedAt", "TEXT");
                EnsureColumn(connection, "Peers", "RevokedBy", "TEXT");
                EnsureColumn(connection, "Peers", "Reason", "TEXT");
                EnsureColumn(connection, "Peers", "AppVersion", "TEXT");
                EnsureColumn(connection, "Peers", "ProtocolVersion", "TEXT");
                EnsureColumn(connection, "Peers", "Capabilities", "TEXT");
            }

            public List<PeerAuditEvent> LoadPeerAudit(string url, int limit = 100)
            {
                var events = new List<PeerAuditEvent>();
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT Url, COALESCE(Actor, ''), Action, COALESCE(OldValue, ''), COALESCE(NewValue, ''),
                       COALESCE(Reason, ''), COALESCE(CorrelationId, ''), TimestampUtc
                FROM PeerAuditLog
                WHERE Url = $url
                ORDER BY TimestampUtc DESC, Id DESC
                LIMIT $limit";
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    events.Add(new PeerAuditEvent(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetString(7)));
                }

                return events;
            }

            private void AppendPeerAudit(SqliteConnection connection, string url, string? actor, string action, string oldValue, string newValue, string? reason, string correlationId)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT INTO PeerAuditLog (Url, Actor, Action, OldValue, NewValue, Reason, CorrelationId, TimestampUtc)
                VALUES ($url, $actor, $action, $oldValue, $newValue, $reason, $correlationId, $timestamp)";
                cmd.Parameters.AddWithValue("$url", url);
                cmd.Parameters.AddWithValue("$actor", actor ?? string.Empty);
                cmd.Parameters.AddWithValue("$action", action);
                cmd.Parameters.AddWithValue("$oldValue", oldValue ?? string.Empty);
                cmd.Parameters.AddWithValue("$newValue", newValue ?? string.Empty);
                cmd.Parameters.AddWithValue("$reason", reason ?? string.Empty);
                cmd.Parameters.AddWithValue("$correlationId", correlationId);
                cmd.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            private static string NormalizeMembershipStatus(string status)
            {
                string trimmed = status.Trim();
                string[] allowed =
                [
                    PeerMembershipStatuses.PendingApproval,
                    PeerMembershipStatuses.Approved,
                    PeerMembershipStatuses.Rejected,
                    PeerMembershipStatuses.Revoked,
                    PeerMembershipStatuses.Offline,
                    PeerMembershipStatuses.Stale,
                    PeerMembershipStatuses.Incompatible,
                    PeerMembershipStatuses.ConsensusCandidate
                ];
                return allowed.FirstOrDefault(value => string.Equals(value, trimmed, StringComparison.OrdinalIgnoreCase))
                    ?? PeerMembershipStatuses.PendingApproval;
            }

            private static string Fingerprint(string? publicKey)
            {
                if (string.IsNullOrWhiteSpace(publicKey)) return string.Empty;
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(publicKey));
                return Convert.ToHexString(hash[..8]).ToLowerInvariant();
            }

            private static string LoadPeerStatus(SqliteConnection connection, string url)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(MembershipStatus, '') FROM Peers WHERE Url = $url LIMIT 1";
                cmd.Parameters.AddWithValue("$url", url);
                return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
            }

            private static string LoadPeerCapabilities(SqliteConnection connection, string url)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(Capabilities, '') FROM Peers WHERE Url = $url LIMIT 1";
                cmd.Parameters.AddWithValue("$url", url);
                return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
            }

            private static void EnsureIntentOutboxColumns(SqliteConnection connection)
            {
                EnsureColumn(connection, "IntentOutbox", "CommittedBlockIndex", "INTEGER");
                EnsureColumn(connection, "IntentOutbox", "ProposalId", "TEXT");
            }

            private static void EnsureBlockColumns(SqliteConnection connection, string tableName)
            {
                EnsureColumn(connection, tableName, "TimestampUnixSeconds", "INTEGER NOT NULL DEFAULT 0");
            }

            private static void EnsureBlockColumns(SqliteConnection connection, SqliteTransaction transaction, string tableName)
            {
                EnsureColumn(connection, transaction, tableName, "TimestampUnixSeconds", "INTEGER NOT NULL DEFAULT 0");
            }

            private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
            {
                EnsureColumn(connection, null, tableName, columnName, definition);
            }

            private static void EnsureColumn(SqliteConnection connection, SqliteTransaction? transaction, string tableName, string columnName, string definition)
            {
                using (var check = connection.CreateCommand())
                {
                    check.Transaction = transaction;
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
                alter.Transaction = transaction;
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
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        using var connection = new SqliteConnection(ConnectionString);
                        connection.Open();
                        var cmd = connection.CreateCommand();
                        string safeProjectId = ChannelName.Normalize(projectId);
                        string rawProjectId = projectId ?? string.Empty;
                        cmd.CommandText = "SELECT Role FROM ProjectMembers WHERE (ProjectId = $proj OR ProjectId = $raw) AND UserName = $user";
                        cmd.Parameters.AddWithValue("$proj", safeProjectId);
                        cmd.Parameters.AddWithValue("$raw", rawProjectId);
                        cmd.Parameters.AddWithValue("$user", userName);
                        string role = DecryptString(cmd.ExecuteScalar()?.ToString());
                        return string.IsNullOrWhiteSpace(role) ? "None" : role;
                    }
                    catch (SqliteException ex) when (IsDatabaseOpenFailure(ex) && attempt < 3)
                    {
                        Thread.Sleep(50 * attempt);
                    }
                }

                return "None";
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
                    cmdInit.CommandText = $"CREATE TABLE IF NOT EXISTS {tableName} (IndexId INTEGER PRIMARY KEY, Timestamp TEXT, TimestampUnixSeconds INTEGER NOT NULL DEFAULT 0, Data TEXT, PreviousHash TEXT, Hash TEXT, ValidatorPublicKey TEXT, Signature TEXT, Nonce INTEGER)";
                    cmdInit.ExecuteNonQuery();
                    EnsureBlockColumns(connection, tableName);

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT IndexId, Timestamp, TimestampUnixSeconds, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce FROM {tableName} ORDER BY IndexId ASC";

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
                    cmd.CommandText = $"SELECT IndexId, Timestamp, TimestampUnixSeconds, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce FROM {tableName} ORDER BY IndexId DESC LIMIT 1";

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
                    cmd.CommandText = $"SELECT IndexId, Timestamp, TimestampUnixSeconds, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce FROM {tableName} ORDER BY IndexId DESC LIMIT $limit";
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
                cmdInit.CommandText = $"CREATE TABLE IF NOT EXISTS {tableName} (IndexId INTEGER PRIMARY KEY, Timestamp TEXT, TimestampUnixSeconds INTEGER NOT NULL DEFAULT 0, Data TEXT, PreviousHash TEXT, Hash TEXT, ValidatorPublicKey TEXT, Signature TEXT, Nonce INTEGER)";
                cmdInit.ExecuteNonQuery();
                EnsureBlockColumns(connection, tableName);
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
                    TimestampUnixSeconds = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2)),
                    Data = DecryptString(reader.IsDBNull(3) ? "" : (reader.GetValue(3)?.ToString() ?? "")),
                    PreviousHash = reader.IsDBNull(4) ? "" : (reader.GetValue(4)?.ToString() ?? ""),
                    Hash = reader.IsDBNull(5) ? "" : (reader.GetValue(5)?.ToString() ?? ""),
                    ValidatorPublicKey = DecryptString(reader.IsDBNull(6) ? "" : (reader.GetValue(6)?.ToString() ?? "")),
                    Signature = DecryptString(reader.IsDBNull(7) ? "" : (reader.GetValue(7)?.ToString() ?? "")),
                    Nonce = reader.IsDBNull(8) ? 0 : Convert.ToInt64(reader.GetValue(8)),
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

                EnsureBlockColumns(connection, transaction, tableName);

                cmdBlock.CommandText = $"INSERT OR IGNORE INTO {tableName} (IndexId, Timestamp, TimestampUnixSeconds, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce) VALUES ($idx, $time, $timeUnix, $data, $prev, $hash, $val, $sig, $nonce)";
                cmdBlock.Parameters.AddWithValue("$idx", block.Index);
                cmdBlock.Parameters.AddWithValue("$time", block.Timestamp.ToString("O"));
                cmdBlock.Parameters.AddWithValue("$timeUnix", block.TimestampUnixSeconds);
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

            public void SaveUserPublicKey(string userName, string publicKey)
            {
                if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(publicKey))
                {
                    return;
                }

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO Users (UserName, PublicKey) VALUES ($user, $publicKey)";
                cmd.Parameters.AddWithValue("$user", userName.Trim());
                cmd.Parameters.AddWithValue("$publicKey", EncryptString(publicKey));
                cmd.ExecuteNonQuery();
            }

            public void ClearMempool()
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM Mempool";
                cmd.ExecuteNonQuery();
            }

            public void SaveFinalityMetadata(BlockFinalityMetadata metadata)
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT OR REPLACE INTO BlockFinalityMetadata
                    (BlockHash, ChannelId, FinalityMode, RaftLogIndex, RaftTerm, CommittedAtUtc)
                VALUES
                    ($hash, $channel, $mode, $raftIndex, $raftTerm, $committedAt);";
                cmd.Parameters.AddWithValue("$hash", metadata.BlockHash);
                cmd.Parameters.AddWithValue("$channel", SanitizeChannelName(metadata.ChannelId));
                cmd.Parameters.AddWithValue("$mode", metadata.FinalityMode);
                cmd.Parameters.AddWithValue("$raftIndex", metadata.RaftLogIndex.HasValue ? metadata.RaftLogIndex.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$raftTerm", metadata.RaftTerm.HasValue ? metadata.RaftTerm.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$committedAt", metadata.CommittedAtUtc.ToUniversalTime().ToString("O"));
                cmd.ExecuteNonQuery();
            }

            public BlockFinalityMetadata? GetFinalityMetadata(string blockHash)
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT BlockHash, ChannelId, FinalityMode, RaftLogIndex, RaftTerm, CommittedAtUtc
                FROM BlockFinalityMetadata
                WHERE BlockHash = $hash
                LIMIT 1;";
                cmd.Parameters.AddWithValue("$hash", blockHash);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                return new BlockFinalityMetadata(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    DateTime.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var committedAt)
                        ? committedAt.ToUniversalTime()
                        : DateTime.MinValue);
            }

            public bool HasFinalityMetadata(string blockHash)
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM BlockFinalityMetadata WHERE BlockHash = $hash LIMIT 1";
                cmd.Parameters.AddWithValue("$hash", blockHash);
                return cmd.ExecuteScalar() != null;
            }

            public void SaveContributionProof(string blockHash, ContributionProof proof)
            {
                if (string.IsNullOrWhiteSpace(blockHash))
                {
                    return;
                }

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                INSERT OR REPLACE INTO BlockContributionProofs
                    (BlockHash, ProjectId, Epoch, ProducerPublicKey, ProducerScore, ScoreSnapshotHash, EvidenceBlockHashesJson)
                VALUES
                    ($hash, $project, $epoch, $producer, $score, $snapshot, $evidence);";
                cmd.Parameters.AddWithValue("$hash", blockHash);
                cmd.Parameters.AddWithValue("$project", ChannelName.Normalize(proof.ProjectId));
                cmd.Parameters.AddWithValue("$epoch", proof.Epoch);
                cmd.Parameters.AddWithValue("$producer", EncryptString(proof.ProducerPublicKey));
                cmd.Parameters.AddWithValue("$score", proof.ProducerScore);
                cmd.Parameters.AddWithValue("$snapshot", proof.ScoreSnapshotHash);
                cmd.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(proof.EvidenceBlockHashes));
                cmd.ExecuteNonQuery();
            }

            public ContributionProof? GetContributionProof(string blockHash)
            {
                if (string.IsNullOrWhiteSpace(blockHash))
                {
                    return null;
                }

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                SELECT ProjectId, Epoch, ProducerPublicKey, ProducerScore, ScoreSnapshotHash, EvidenceBlockHashesJson
                FROM BlockContributionProofs
                WHERE BlockHash = $hash
                LIMIT 1;";
                cmd.Parameters.AddWithValue("$hash", blockHash);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                string evidenceJson = reader.GetString(5);
                string[] evidence = Array.Empty<string>();
                try
                {
                    evidence = JsonSerializer.Deserialize<string[]>(evidenceJson) ?? Array.Empty<string>();
                }
                catch
                {
                    evidence = Array.Empty<string>();
                }

                return new ContributionProof(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    DecryptString(reader.GetString(2)),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    evidence);
            }
        }
    }
