using System;
using System.Text.Json;
using Blockchain.Core.Constants;

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

                Console.WriteLine($"\n[SmartContract Debug] Data turned back: {data}");

                var evt = JsonSerializer.Deserialize<ContractTaskEvent>(data);
                if (evt == null || string.IsNullOrEmpty(evt.ProjectId))
                {
                    Console.WriteLine("[SmartContract Debug] ❌ Reject: Parsing failed, JSON or ProjectId is empty.");
                    return false;
                }

                string expectedPublicKey = db.GetUserPublicKey(evt.User);

                if (!string.IsNullOrEmpty(expectedPublicKey) && expectedPublicKey != senderPublicKey)
                {
                    Console.WriteLine($"[SmartContract] ❌ Warning! Identity spoofing detected. Claimed User: {evt.User}");
                    return false;
                }

                Console.WriteLine($"[SmartContract Debug] Requesting role for user '{evt.User}' in project '{evt.ProjectId}'...");
                string senderRole = db.GetUserRole(evt.ProjectId, evt.User);
                Console.WriteLine($"[SmartContract Debug] Database returned role: '{senderRole}'");

                if (evt.Type == "AssignRole")
                {
                    if (string.IsNullOrEmpty(evt.TargetUser)) return false;

                    if (db.IsProjectExists(evt.ProjectId))
                    {
                        if (senderRole != "Owner" && senderRole != "Manager")
                        {
                            Console.WriteLine($"[SmartContract] RBAC Denied: {evt.User} is not an Admin in {evt.ProjectId}");
                            return false;
                        }

                        if (senderRole == "Manager" && evt.Role == "Owner")
                        {
                            Console.WriteLine($"[SmartContract] RBAC Denied: Managers cannot create Owners.");
                            return false;
                        }
                    }
                }

                if (evt.Type == "Move" || evt.Type == "Create")
                {
                    if (db.IsProjectExists(evt.ProjectId) && senderRole == "None")
                    {
                        Console.WriteLine($"[SmartContract] RBAC Denied: {evt.User} has no access to {evt.ProjectId}");
                        return false;
                    }
                }

                Console.WriteLine("[SmartContract Debug] ✅ Contract successfully verified!");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SmartContract FATAL ERROR] An unhandled code error occurred: {ex.Message}");
                return false;
            }
        }
    }
}