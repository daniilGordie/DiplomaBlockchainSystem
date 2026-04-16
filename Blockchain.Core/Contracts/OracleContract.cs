using System.Text.Json;

namespace Blockchain.Core.Contracts
{
    public class OracleContract : ISmartContract
    {
        public string Name => "TrustedOracleContract";

        public bool Validate(string data, string senderPublicKey, DatabaseManager db)
        {
            try
            {
                if (data.Contains("\"Source\":\"GitHub"))
                {
                    using var doc = JsonDocument.Parse(data);
                    if (!doc.RootElement.TryGetProperty("CommitHash", out var hash)) return false;
                    return !string.IsNullOrEmpty(hash.GetString());
                }

                if (data.Contains("\"Source\":\"ArtifactRegistry\""))
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("FileHash", out var hash))
                    {
                        var h = hash.GetString();
                        return h != null && h.Length == 64; 
                    }
                    return false;
                }

                return true; 
            }
            catch { return false; }
        }
    }
}