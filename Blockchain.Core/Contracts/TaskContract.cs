using System;
using System.Text.Json;
using Blockchain.Core.Constants;
using Blockchain.Core;

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

                if (type == "CreateProject")
                {
                    return true;
                }

                string projectId = root.TryGetProperty("ProjectId", out var p) ? p.GetString() ?? "" : "";
                string user = root.TryGetProperty("User", out var u) ? u.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(type))
                {
                    return false;
                }

                bool isProjectScopedMutation =
                    type == "Create" || type == "Update" || type == "Move" ||
                    type == "CreateDocument" || type == "UpdateDocument" ||
                    type == "CodeCommit" || type == "Register" || type == "Transfer" ||
                    type == "CreateProposal" || type == "CastVote" || type == "AssignRole";

                if (isProjectScopedMutation && string.IsNullOrWhiteSpace(projectId))
                {
                    Console.WriteLine("[SmartContract Debug] Rejected: project id is required for project-scoped event.");
                    return false;
                }

                if (!string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(user))
                {
                    if ((type == "Create" || type == "Update" || type == "Move" ||
                         type == "CreateDocument" || type == "UpdateDocument" ||
                         type == "CodeCommit" || type == "Register" || type == "Transfer" ||
                         type == "CreateProposal" || type == "CastVote") && projectId == "System")
                    {
                        Console.WriteLine("[SmartContract Debug] Rejected: project operations are not allowed in System channel.");
                        return false;
                    }

                    string role = db.GetUserRole(projectId, user);
                    Console.WriteLine($"[SmartContract Debug] Checking role for '{user}' in '{projectId}': {role}");

                    if (role == "None" && projectId != "System")
                    {
                        Console.WriteLine("[SmartContract Debug] Rejected: Access Denied!");
                        return false;
                    }
                }

                return true;
            }
            catch { return false; }
        }
    }
}
