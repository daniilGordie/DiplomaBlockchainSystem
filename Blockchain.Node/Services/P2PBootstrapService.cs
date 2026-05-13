using Blockchain.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blockchain.Node.Services
{
    public class P2PBootstrapService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly BlockchainManager _blockchainManager;
        private readonly P2PNetworkService _p2pService;
        private readonly ILogger<P2PBootstrapService> _logger;
        private readonly DatabaseManager _db;

        public P2PBootstrapService(
            IConfiguration configuration,
            BlockchainManager blockchainManager,
            P2PNetworkService p2pService,
            ILogger<P2PBootstrapService> logger)
        {
            _configuration = configuration;
            _blockchainManager = blockchainManager;
            _p2pService = p2pService;
            _logger = logger;

            _db = new DatabaseManager(GetNodeDatabaseName(configuration), GetRequiredConfiguration(configuration, "NodeDbPassword"));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            var peers = _configuration.GetSection("P2P:BootstrapPeers").Get<string[]>() ?? Array.Empty<string>();
            var persistedPeers = _db.LoadPeers();

            foreach (var peerUrl in peers.Concat(persistedPeers).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (IsSelf(peerUrl)) continue;

                _p2pService.AddPeer(peerUrl);
                _db.SavePeer(peerUrl);
                await SyncFromPeerAsync(peerUrl);
            }
        }

        private async Task SyncFromPeerAsync(string peerUrl)
        {
            try
            {
                var channels = await _p2pService.FetchKnownChannelsAsync(peerUrl);
                if (!channels.Contains("System", StringComparer.OrdinalIgnoreCase))
                {
                    channels.Insert(0, "System");
                }

                foreach (var channelId in channels.OrderBy(channel => channel == "System" ? 0 : 1))
                {
                    var blocks = await _p2pService.FetchChainAsync(peerUrl, channelId);
                    var chain = blocks.Select(ToBlock).OrderBy(block => block.Index).ToList();

                    if (_blockchainManager.TryAdoptChain(channelId, chain))
                    {
                        continue;
                    }

                    foreach (var block in chain)
                    {
                        if (!_db.BlockExists(block.Hash, block.ChannelId))
                        {
                            _blockchainManager.ProcessPeerBlock(block);
                        }
                    }
                }

                _db.SavePeer(peerUrl);
            }
            catch (Exception ex)
            {
                _db.MarkPeerFailure(peerUrl);
                _logger.LogWarning("[P2P] Bootstrap sync from {PeerUrl} failed: {Message}", peerUrl, ex.Message);
            }
        }

        private bool IsSelf(string peerUrl)
        {
            string? publicUrl = _configuration["P2P:PublicUrl"];
            return !string.IsNullOrWhiteSpace(publicUrl)
                && string.Equals(publicUrl.TrimEnd('/'), peerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        private static Block ToBlock(BlockModel model)
        {
            return new Block
            {
                Index = model.Index,
                Data = model.Data,
                PreviousHash = model.PreviousHash,
                Hash = model.Hash,
                Timestamp = DateTime.Parse(model.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind),
                ValidatorPublicKey = model.ValidatorPublicKey,
                Signature = model.Signature,
                Nonce = model.Nonce,
                ChannelId = string.IsNullOrWhiteSpace(model.ChannelId) ? "System" : model.ChannelId
            };
        }

        private static string GetRequiredConfiguration(IConfiguration configuration, string key)
        {
            string? value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"{key} is not configured. Set it via .NET user-secrets or environment variables.");
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
