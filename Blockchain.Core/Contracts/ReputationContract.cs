using System;
using System.Text.Json;

namespace Blockchain.Core.Contracts
{
    public class ReputationContract : ISmartContract
    {
        public string Name => "ReputationToken (NXP)";

        public bool Validate(string data, string senderPublicKey, DatabaseManager db)
        {
            try
            {
                if (!data.Contains("\"Type\":")) return true;

                var evt = JsonSerializer.Deserialize<ContractTaskEvent>(data);
                if (evt == null) return false;

                string expectedPublicKey = db.GetUserPublicKey(evt.User);
                if (!string.IsNullOrEmpty(expectedPublicKey) && expectedPublicKey != senderPublicKey)
                {
                    Console.WriteLine($"[SmartContract] Identity spoofing detected! User: {evt.User}");
                    return false;
                }

                if (evt.Type == "Mint")
                {
                    Console.WriteLine("[SmartContract] Minting rejected: Only Proof-of-Work events can mint NXP.");
                    return false;
                }

                if (evt.Type == "Transfer")
                {
                    if (evt.Amount <= 0) return false;
                    if (string.IsNullOrEmpty(evt.TargetUser)) return false;

                    int currentBalance = db.GetUserBalance(evt.User);

                    if (currentBalance < evt.Amount)
                    {
                        Console.WriteLine($"[SmartContract] Transfer rejected: {evt.User} has insufficient funds.");
                        return false;
                    }
                }

                return true;
            }
            catch { return false; }
        }
    }
}