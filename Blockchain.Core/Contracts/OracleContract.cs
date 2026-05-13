using System;
using System.Text.Json;
using Blockchain.Core.Constants;
using Blockchain.Core;

namespace Blockchain.Core.Contracts
{
    public class OracleContract : ISmartContract
    {
        public string Name => "TrustedOracleContract";

        public bool Validate(string data, string senderPublicKey, DatabaseManager db)
        {
            try
            {
                // Oracle signature is required only for trusted external feeds.
                bool isOracleEvent = data.Contains("\"Source\":\"GitEvent\"") ||
                                     data.Contains("\"Source\":\"GitHub\"") ||
                                     data.Contains("\"Source\":\"ArtifactRegistry\"");

                if (isOracleEvent)
                {
                    if (string.IsNullOrEmpty(NetworkParameters.TrustedOraclePublicKey))
                    {
                        Console.WriteLine("[OracleContract] Critical Error: Oracle public key is not loaded into the network parameters!");
                        return false;
                    }

                    if (senderPublicKey != NetworkParameters.TrustedOraclePublicKey)
                    {
                        Console.WriteLine($"[OracleContract] Rejected: Attempted to spoof oracle data from key: {senderPublicKey}");
                        return false;
                    }

                    using var doc = JsonDocument.Parse(data);

                    if (data.Contains("\"Type\":\"CodeCommit\"") ||
                        data.Contains("\"Source\":\"GitEvent\"") ||
                        data.Contains("\"Source\":\"GitHub\""))
                    {
                        if (!doc.RootElement.TryGetProperty("CommitHash", out var hash)) return false;
                        return !string.IsNullOrEmpty(hash.GetString());
                    }

                    if (data.Contains("\"Source\":\"ArtifactRegistry\""))
                    {
                        if (doc.RootElement.TryGetProperty("FileHash", out var hash))
                        {
                            var h = hash.GetString();
                            return !string.IsNullOrEmpty(h) && h.Length >= 46;
                        }
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OracleContract] Parsing error: {ex.Message}");
                return false;
            }
        }
    }
}
