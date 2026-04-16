using System.Text.Json;

namespace Blockchain.Core.Contracts
{
    public class TaskContract : ISmartContract
    {
        public string Name => "TaskFlow";

        public bool Validate(string data, string senderPublicKey, DatabaseManager db)
        {
            try
            {
                if (!data.Contains("\"Type\":")) return true;

                var evt = JsonSerializer.Deserialize<ContractTaskEvent>(data);
                if (evt == null) return false;

                if (string.IsNullOrWhiteSpace(evt.ProjectId)) return false;

                if (evt.Type == "Create")
                {
                    if (string.IsNullOrWhiteSpace(evt.Title)) return false;
                    if (evt.Status != 0) return false;
                    return true;
                }

                if (evt.Type == "Move")
                {
                    if (evt.Status < 0 || evt.Status > 2) return false;
                    return true;
                }

                return true;
            }
            catch { return false; }
        }
    }
}