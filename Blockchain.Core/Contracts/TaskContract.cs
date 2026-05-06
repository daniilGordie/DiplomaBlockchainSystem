using System;
using System.Text.Json;
using Blockchain.Core.Constants;

namespace Blockchain.Core.Contracts
{
    public class TaskContract : ISmartContract
    {
        public string Name => "TaskFlow";

        public bool Validate(string data, string senderPublicKey, DatabaseManager db)
        {
            try
            {
                if (NetworkParameters.IsGenesisModeEnabled)
                {
                    return true;
                }

                if (!data.Contains("\"Type\":")) return true;

                using var doc = System.Text.Json.JsonDocument.Parse(data);
                var root = doc.RootElement;
                string type = root.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "";

                if (type == "CreateProject" || type == "CodeCommit" || type == "Register")
                {
                    return true;
                }

                string projectId = root.TryGetProperty("ProjectId", out var p) ? p.GetString() ?? "" : "";
                string user = root.TryGetProperty("User", out var u) ? u.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(user))
                {
                    string role = db.GetUserRole(projectId, user);
                    Console.WriteLine($"[SmartContract Debug] Checking role for '{user}' in '{projectId}': {role}");

                    if (role == "None" && projectId != "System")
                    {
                        Console.WriteLine("[SmartContract Debug] ❌ Rejected: Access Denied!");
                        return false;
                    }
                }

                return true;
            }
            catch { return false; }
        }
    }
}