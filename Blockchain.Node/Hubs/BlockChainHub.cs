using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Blockchain.Core;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Linq;

namespace Blockchain.Node.Hubs
{
    public class BlockchainHub : Hub
    {
        private readonly IConfiguration _configuration;
        private static readonly ConcurrentDictionary<string, string> _connectionUsers = new();

        public BlockchainHub(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task JoinProject(string projectId)
        {
            string channel = string.IsNullOrWhiteSpace(projectId) ? "System" : projectId;
            if (channel == "System")
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, "System");
                return;
            }

            if (!_connectionUsers.TryGetValue(Context.ConnectionId, out var userName) || string.IsNullOrWhiteSpace(userName))
            {
                return;
            }

            var db = new DatabaseManager(GetNodeDatabaseName(_configuration), GetRequiredConfiguration(_configuration, "NodeDbPassword"));
            string role = db.GetUserRole(channel, userName);
            if (role == "None")
            {
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, channel);
        }

        // CHANGED: cryptographic registration to mitigate nickname impersonation in realtime groups.
        public async Task<bool> RegisterUser(string userName, string publicKeyBase64, string signatureBase64)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(publicKeyBase64) || string.IsNullOrWhiteSpace(signatureBase64))
            {
                return false;
            }

            string challenge = $"REGISTER:{Context.ConnectionId}:{userName}";

            try
            {
                byte[] signatureBytes = Convert.FromBase64String(signatureBase64);
                if (signatureBytes.Length != 64) return false;

                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
                bool valid = ecdsa.VerifyData(
                    Encoding.UTF8.GetBytes(challenge),
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

                if (!valid) return false;

                await Groups.AddToGroupAsync(Context.ConnectionId, $"USER_{userName}");
                _connectionUsers[Context.ConnectionId] = userName;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            _connectionUsers.TryRemove(Context.ConnectionId, out _);
            return base.OnDisconnectedAsync(exception);
        }

        private static string GetRequiredConfiguration(IConfiguration configuration, string key)
        {
            string? value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{key} is not configured.");
            }

            return value;
        }

        private static string GetNodeDatabaseName(IConfiguration configuration)
        {
            string port = configuration["Urls"]?.Split(':').LastOrDefault()?.Replace("/", "") ?? "5041";
            string? configuredDbName = configuration.GetConnectionString("DefaultNodeDb");

            if (string.IsNullOrWhiteSpace(configuredDbName) ||
                (configuredDbName == "nexus_node_5041.db" && port != "5041"))
            {
                return $"nexus_node_{port}.db";
            }

            return configuredDbName;
        }
    }
}
