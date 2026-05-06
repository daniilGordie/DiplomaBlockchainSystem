using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Blockchain.Core
{
    public class DatabaseManager
    {
        public string DbFileName { get; private set; }
        private string ConnectionString => $"Data Source={DbFileName}";

        public DatabaseManager(string dbName)
        {
            if (string.IsNullOrWhiteSpace(dbName))
            {
                throw new ArgumentException("Database name cannot be empty!", nameof(dbName));
            }

            DbFileName = dbName;
            InitializeDatabase();
        }

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
            }
        }

        public static string SanitizeChannelName(string channelId)
        {
            if (string.IsNullOrWhiteSpace(channelId)) return "System";
            var safeName = new string(channelId.Where(char.IsLetterOrDigit).ToArray());
            return string.IsNullOrEmpty(safeName) ? "System" : safeName;
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

                        var cmdBlock = connection.CreateCommand();
                        cmdBlock.Transaction = transaction;

                        cmdBlock.CommandText = $"INSERT INTO {tableName} (IndexId, Timestamp, Data, PreviousHash, Hash, ValidatorPublicKey, Signature, Nonce) VALUES ($idx, $time, $data, $prev, $hash, $val, $sig, $nonce)";
                        cmdBlock.Parameters.AddWithValue("$idx", block.Index);
                        cmdBlock.Parameters.AddWithValue("$time", block.Timestamp.ToString("O"));
                        cmdBlock.Parameters.AddWithValue("$data", block.Data);
                        cmdBlock.Parameters.AddWithValue("$prev", block.PreviousHash);
                        cmdBlock.Parameters.AddWithValue("$hash", block.Hash);
                        cmdBlock.Parameters.AddWithValue("$val", block.ValidatorPublicKey ?? "");
                        cmdBlock.Parameters.AddWithValue("$sig", block.Signature ?? "");
                        cmdBlock.Parameters.AddWithValue("$nonce", block.Nonce);
                        cmdBlock.ExecuteNonQuery();

                        if (block.Data.Trim().StartsWith("{") || block.Data.Trim().StartsWith("["))
                        {
                            using var doc = JsonDocument.Parse(block.Data);
                            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            {
                                UpdateStateIndex(connection, transaction, doc.RootElement, block.ValidatorPublicKey);
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
                cmdUser.Parameters.AddWithValue("$pk", validatorPubKey);
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
                    cmdOwner.CommandText = "INSERT INTO ProjectMembers (ProjectId, UserName, Role) VALUES ($p, $u, 'Owner')";
                    cmdOwner.Parameters.AddWithValue("$p", projRole);
                    cmdOwner.Parameters.AddWithValue("$u", sender);
                    cmdOwner.ExecuteNonQuery();
                }

                if (type == "AssignRole")
                {
                    string target = GetStringSafe(root, "TargetUser");
                    string role = GetStringSafe(root, "Role", "Worker");
                    if (!string.IsNullOrEmpty(target))
                    {
                        var cmdRole = conn.CreateCommand();
                        cmdRole.Transaction = tx;
                        cmdRole.CommandText = "INSERT OR REPLACE INTO ProjectMembers (ProjectId, UserName, Role) VALUES ($p, $u, $r)";
                        cmdRole.Parameters.AddWithValue("$p", projRole);
                        cmdRole.Parameters.AddWithValue("$u", target);
                        cmdRole.Parameters.AddWithValue("$r", role);
                        cmdRole.ExecuteNonQuery();
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
                    cmd.Parameters.AddWithValue("$creator", sender);

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
                cmd.Parameters.AddWithValue("$title", title);
                cmd.Parameters.AddWithValue("$st", status);
                cmd.Parameters.AddWithValue("$assignee", string.IsNullOrEmpty(assignee) ? "None" : assignee);
                cmd.Parameters.AddWithValue("$proj", proj);
                cmd.Parameters.AddWithValue("$desc", desc);
                cmd.Parameters.AddWithValue("$parent", parent);
                cmd.Parameters.AddWithValue("$branch", branch);
                cmd.ExecuteNonQuery();
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
            return cmd.ExecuteScalar()?.ToString() ?? "None";
        }

        public List<Block> LoadChain(string channelId = "System")
        {
            var chain = new List<Block>();

            string safeChannel = new string(channelId.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (string.IsNullOrEmpty(safeChannel)) safeChannel = "System";

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
                        chain.Add(new Block
                        {
                            Index = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0)),
                            Timestamp = reader.IsDBNull(1) ? DateTime.MinValue : DateTime.Parse(reader.GetValue(1).ToString()),
                            Data = reader.IsDBNull(2) ? "" : reader.GetValue(2).ToString(),
                            PreviousHash = reader.IsDBNull(3) ? "" : reader.GetValue(3).ToString(),
                            Hash = reader.IsDBNull(4) ? "" : reader.GetValue(4).ToString(),
                            ValidatorPublicKey = reader.IsDBNull(5) ? "" : reader.GetValue(5).ToString(),
                            Signature = reader.IsDBNull(6) ? "" : reader.GetValue(6).ToString(),
                            Nonce = reader.IsDBNull(7) ? 0 : Convert.ToInt64(reader.GetValue(7)),
                            ChannelId = safeChannel
                        });
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

        public void SaveToMempool(string txId, string json)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Mempool (
                    TxId TEXT PRIMARY KEY,
                    Data TEXT NOT NULL,
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
                );
                INSERT OR IGNORE INTO Mempool (TxId, Data) VALUES (@txId, @data);";

            command.Parameters.AddWithValue("@txId", txId);
            command.Parameters.AddWithValue("@data", json);
            command.ExecuteNonQuery();
        }

        public List<string> GetMempoolTransactions(int limit)
        {
            var result = new List<string>();
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT PayloadJson FROM Mempool ORDER BY Timestamp ASC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
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

        public string GetUserPublicKey(string userName)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT PublicKey FROM Users WHERE UserName = $user";
            cmd.Parameters.AddWithValue("$user", userName);
            return cmd.ExecuteScalar()?.ToString();
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