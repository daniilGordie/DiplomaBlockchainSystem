using System;
using System.Text.Json;
using Blockchain.Core;
using Blockchain.Core.Constants;

namespace Blockchain.Core.Contracts
{
    public class ReputationContract : ISmartContract
    {
        public string Name => "ReputationToken (NXP)";

        public bool Validate(string data, string senderPublicKey, ISmartContractStateReader state)
        {
            try
            {
                if (!data.Contains("\"Type\":")) return true;

                var evt = JsonSerializer.Deserialize<ContractTaskEvent>(data);
                if (evt == null) return false;

                bool isTrustedOracleFeed = IsTrustedOracleFeed(data, senderPublicKey);
                string? expectedPublicKey = state.GetUserPublicKey(evt.User);
                if (!isTrustedOracleFeed &&
                    !string.IsNullOrEmpty(expectedPublicKey) &&
                    expectedPublicKey != senderPublicKey)
                {
                    Console.WriteLine($"[SmartContract] Identity spoofing detected! User: {evt.User}");
                    return false;
                }

                if (evt.Type == "Mint")
                {
                    Console.WriteLine("[SmartContract] Minting rejected: Only verified contribution events can mint NXP.");
                    return false;
                }

                if (evt.Type == "Transfer")
                {
                    if (evt.Amount <= 0) return false;
                    if (string.IsNullOrEmpty(evt.TargetUser)) return false;

                    int currentBalance = state.GetUserBalance(evt.User);

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
                       (source == "GitEvent" || source == "GitHub" || source == "ArtifactRegistry");
            }
            catch
            {
                return false;
            }
        }
    }
}
