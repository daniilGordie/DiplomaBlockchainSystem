using System;
using System.Security.Cryptography;
using System.Text.Json;
using Blockchain.Core.Constants;
using Blockchain.Core;

namespace Blockchain.Core.Contracts
{
    public class AccessControlContract : ISmartContract
    {
        public string Name => "AccessControl (RBAC)";

        public bool Validate(string data, string senderPublicKey, DatabaseManager db)
        {
            try
            {
                if (NetworkParameters.IsGenesisModeEnabled)
                {
                    return true;
                }

                if (!data.Contains("\"Type\":")) return true;

                Console.WriteLine($"[SmartContract Debug] Validating payload: {DescribePayload(data)}");

                var evt = JsonSerializer.Deserialize<ContractTaskEvent>(data);
                if (evt == null || string.IsNullOrEmpty(evt.ProjectId))
                {
                    Console.WriteLine("[SmartContract Debug] Reject: Parsing failed, JSON or ProjectId is empty.");
                    return false;
                }

                bool isTrustedOracleFeed = IsTrustedOracleFeed(data, senderPublicKey);

                bool requiresActorIdentity =
                    evt.Type == "CreateProject" ||
                    evt.Type == "AssignRole" ||
                    evt.Type == "Move" || evt.Type == "Create" || evt.Type == "Update" ||
                    evt.Type == "CreateDocument" || evt.Type == "UpdateDocument" ||
                    evt.Type == "CreateProposal" || evt.Type == "CastVote" ||
                    evt.Type == "CodeCommit" || evt.Type == "Register" || evt.Type == "Transfer";

                if (requiresActorIdentity && string.IsNullOrWhiteSpace(evt.User))
                {
                    Console.WriteLine("[SmartContract] RBAC Denied: event requires a non-empty user identity.");
                    return false;
                }

                string? expectedPublicKey = db.GetUserPublicKey(evt.User);
                bool requiresBoundIdentity = requiresActorIdentity && evt.Type != "CreateProject" && !isTrustedOracleFeed;
                if (requiresBoundIdentity && string.IsNullOrWhiteSpace(expectedPublicKey))
                {
                    Console.WriteLine($"[SmartContract] RBAC Denied: user '{evt.User}' does not have a registered public key.");
                    return false;
                }

                if (!isTrustedOracleFeed && !string.IsNullOrEmpty(expectedPublicKey) && expectedPublicKey != senderPublicKey)
                {
                    Console.WriteLine($"[SmartContract] Warning! Identity spoofing detected. Claimed User: {evt.User}");
                    return false;
                }

                Console.WriteLine($"[SmartContract Debug] Requesting role for user '{evt.User}' in project '{evt.ProjectId}'...");
                string senderRole = db.GetUserRole(evt.ProjectId, evt.User);
                Console.WriteLine($"[SmartContract Debug] Database returned role: '{senderRole}'");

                if (evt.Type == "AssignRole")
                {
                    if (string.IsNullOrWhiteSpace(evt.TargetUser)) return false;
                    if (string.IsNullOrWhiteSpace(evt.TargetPublicKey)) return false;
                    if (string.IsNullOrWhiteSpace(evt.ProjectId) || evt.ProjectId == "System")
                    {
                        Console.WriteLine("[SmartContract] RBAC Denied: AssignRole requires a concrete project id.");
                        return false;
                    }

                    if (!db.IsProjectExists(evt.ProjectId))
                    {
                        Console.WriteLine($"[SmartContract] RBAC Denied: cannot assign role in non-existing project '{evt.ProjectId}'.");
                        return false;
                    }

                    try
                    {
                        using var ecdsa = ECDsa.Create();
                        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(evt.TargetPublicKey), out _);
                    }
                    catch
                    {
                        Console.WriteLine("[SmartContract] RBAC Denied: target public key is invalid.");
                        return false;
                    }

                    string? existingTargetKey = db.GetUserPublicKey(evt.TargetUser);
                    if (!string.IsNullOrWhiteSpace(existingTargetKey) && existingTargetKey != evt.TargetPublicKey)
                    {
                        Console.WriteLine($"[SmartContract] RBAC Denied: target user '{evt.TargetUser}' already has another bound key.");
                        return false;
                    }

                    if (senderRole != "Owner" && senderRole != "Manager")
                    {
                        Console.WriteLine($"[SmartContract] RBAC Denied: {evt.User} is not an Admin in {evt.ProjectId}");
                        return false;
                    }

                    if (senderRole == "Manager" && evt.Role == "Owner")
                    {
                        Console.WriteLine("[SmartContract] RBAC Denied: Managers cannot create Owners.");
                        return false;
                    }
                }

                if (evt.Type == "Move" || evt.Type == "Create" || evt.Type == "Update" ||
                    evt.Type == "CreateDocument" || evt.Type == "UpdateDocument" ||
                    evt.Type == "CodeCommit" || evt.Type == "Register" || evt.Type == "Transfer")
                {
                    if (db.IsProjectExists(evt.ProjectId) && senderRole == "None")
                    {
                        Console.WriteLine($"[SmartContract] RBAC Denied: {evt.User} has no access to {evt.ProjectId}");
                        return false;
                    }
                }

                if (evt.Type == "CreateProposal" || evt.Type == "CastVote")
                {
                    if (string.IsNullOrWhiteSpace(evt.ProjectId) || evt.ProjectId == "System")
                    {
                        Console.WriteLine("[SmartContract] Governance denied: project scope is required.");
                        return false;
                    }

                    if (senderRole == "None")
                    {
                        Console.WriteLine($"[SmartContract] Governance denied: {evt.User} has no access to {evt.ProjectId}");
                        return false;
                    }

                    if (evt.Type == "CreateProposal" && string.IsNullOrWhiteSpace(evt.Title))
                    {
                        Console.WriteLine("[SmartContract] Governance denied: proposal title is required.");
                        return false;
                    }

                    if (evt.Type == "CastVote" && string.IsNullOrWhiteSpace(evt.ProposalId))
                    {
                        Console.WriteLine("[SmartContract] Governance denied: proposal id is required.");
                        return false;
                    }
                }

                Console.WriteLine("[SmartContract Debug] Contract successfully verified.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SmartContract FATAL ERROR] An unhandled code error occurred: {ex.Message}");
                return false;
            }
        }

        private static string DescribePayload(string data)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "Unknown" : "Unknown";
                string projectId = root.TryGetProperty("ProjectId", out var projectProp) ? projectProp.GetString() ?? "" : "";
                string taskId = root.TryGetProperty("TaskId", out var taskProp) ? taskProp.GetString() ?? "" : "";

                return string.IsNullOrEmpty(taskId)
                    ? $"Type={type}, ProjectId={projectId}"
                    : $"Type={type}, ProjectId={projectId}, TaskId={taskId}";
            }
            catch
            {
                return "Unparseable JSON payload";
            }
        }

        private static bool IsTrustedOracleFeed(string data, string senderPublicKey)
        {
            if (string.IsNullOrWhiteSpace(NetworkParameters.TrustedOraclePublicKey) ||
                senderPublicKey != NetworkParameters.TrustedOraclePublicKey)
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                string type = root.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                string source = root.TryGetProperty("Source", out var sourceProp) ? sourceProp.GetString() ?? "" : "";

                return (type == "CodeCommit" || type == "Register") &&
                       (source == "GitHub" || source == "ArtifactRegistry");
            }
            catch
            {
                return false;
            }
        }
    }
}
