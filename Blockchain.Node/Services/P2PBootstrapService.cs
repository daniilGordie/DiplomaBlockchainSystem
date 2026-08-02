using Blockchain.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services
{
    public class P2PBootstrapService : BackgroundService
    {
        private readonly P2POptions _options;
        private readonly P2PNetworkService _p2pService;
        private readonly IrohSidecarClient _irohSidecarClient;
        private readonly IPeerStore _peerStore;
        private readonly ILogger<P2PBootstrapService> _logger;

        public P2PBootstrapService(
            IOptions<P2POptions> options,
            P2PNetworkService p2pService,
            IrohSidecarClient irohSidecarClient,
            IPeerStore peerStore,
            ILogger<P2PBootstrapService> logger)
        {
            _options = options.Value;
            _p2pService = p2pService;
            _irohSidecarClient = irohSidecarClient;
            _peerStore = peerStore;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            int intervalSeconds = Math.Clamp(_options.DiscoveryIntervalSeconds, 10, 3600);
            while (!stoppingToken.IsCancellationRequested)
            {
                await DiscoverAndSyncAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
        }

        private async Task DiscoverAndSyncAsync(CancellationToken stoppingToken)
        {
            var discoveredPeers = new Dictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
            string selfIrohUrl = _options.Iroh.Enabled
                ? await _irohSidecarClient.GetPublicUrlAsync(stoppingToken)
                : string.Empty;

            foreach (var peer in _peerStore.LoadPeerInfos())
            {
                AddDiscoveredPeer(discoveredPeers, peer, selfIrohUrl);
            }

            foreach (string bootstrapUrl in _options.NormalizedBootstrapPeers)
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (IsSelf(bootstrapUrl, selfIrohUrl)) continue;

                var bootstrapPeer = new PeerInfo(bootstrapUrl, string.Empty, P2PNodeRole.Bootstrap.ToString());
                AddDiscoveredPeer(discoveredPeers, bootstrapPeer, selfIrohUrl);

                if (_options.Role == P2PNodeRole.Full)
                {
                    await RegisterWithBootstrapAsync(bootstrapUrl);
                }

                await DiscoverPeersAsync(bootstrapUrl, discoveredPeers, selfIrohUrl);
            }

            foreach (var peer in discoveredPeers.Values)
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (IsSelf(peer.Url, selfIrohUrl)) continue;

                _p2pService.AddPeer(peer.Url);
                _peerStore.SavePeer(peer);
            }
        }

        private async Task RegisterWithBootstrapAsync(string bootstrapUrl)
        {
            if (string.IsNullOrWhiteSpace(_options.NormalizedPublicUrl) && !_options.Iroh.Enabled)
            {
                _logger.LogWarning("[P2P] PublicUrl is not configured. Full-node registration with {BootstrapUrl} skipped.", bootstrapUrl);
                return;
            }

            try
            {
                var result = await _p2pService.RegisterWithBootstrapAsync(bootstrapUrl);
                if (result.Success)
                {
                    _peerStore.MarkPeerSeen(bootstrapUrl);
                    _logger.LogInformation("[P2P] Registered node {NodeId} at bootstrap {BootstrapUrl}.", _options.EffectiveNodeId, bootstrapUrl);
                }
                else
                {
                    _logger.LogWarning("[P2P] Bootstrap {BootstrapUrl} rejected registration: {Message}", bootstrapUrl, result.Message);
                }
            }
            catch (Exception ex)
            {
                _peerStore.MarkPeerFailure(bootstrapUrl);
                _logger.LogWarning("[P2P] Registration with bootstrap {BootstrapUrl} failed: {Message}", bootstrapUrl, ex.Message);
            }
        }

        private async Task DiscoverPeersAsync(string bootstrapUrl, Dictionary<string, PeerInfo> discoveredPeers, string selfIrohUrl)
        {
            try
            {
                var directory = await _p2pService.FetchPeerDirectoryAsync(bootstrapUrl);
                foreach (var item in directory)
                {
                    AddDiscoveredPeer(discoveredPeers, new PeerInfo(
                        P2POptions.NormalizeUrl(item.PublicUrl),
                        item.NodeId,
                        string.IsNullOrWhiteSpace(item.Role) ? P2PNodeRole.Full.ToString() : item.Role,
                        string.IsNullOrWhiteSpace(item.LastSeen) ? null : item.LastSeen,
                        string.IsNullOrWhiteSpace(item.LastFailure) ? null : item.LastFailure,
                        item.IsTrusted,
                        item.NodePublicKey), selfIrohUrl);
                }
            }
            catch (Exception ex)
            {
                _peerStore.MarkPeerFailure(bootstrapUrl);
                _logger.LogWarning("[P2P] Peer discovery from {BootstrapUrl} failed: {Message}", bootstrapUrl, ex.Message);
            }
        }

        private void AddDiscoveredPeer(Dictionary<string, PeerInfo> discoveredPeers, PeerInfo peer, string selfIrohUrl)
        {
            string url = P2POptions.NormalizeUrl(peer.Url);
            if (string.IsNullOrWhiteSpace(url) || IsSelf(url, selfIrohUrl))
            {
                return;
            }

            discoveredPeers[url] = peer with { Url = url };
        }

        private bool IsSelf(string peerUrl, string selfIrohUrl)
        {
            string publicUrl = _options.NormalizedPublicUrl;
            string normalizedPeerUrl = P2POptions.NormalizeUrl(peerUrl);
            return (!string.IsNullOrWhiteSpace(publicUrl)
                    && string.Equals(publicUrl, normalizedPeerUrl, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(selfIrohUrl)
                    && string.Equals(selfIrohUrl, normalizedPeerUrl, StringComparison.OrdinalIgnoreCase))
                || (IrohSidecarClient.IsIrohPeerUrl(normalizedPeerUrl)
                    && IrohSidecarClient.IsIrohPeerUrl(selfIrohUrl)
                    && string.Equals(
                        IrohSidecarClient.ParseIrohPeerId(normalizedPeerUrl),
                        IrohSidecarClient.ParseIrohPeerId(selfIrohUrl),
                        StringComparison.OrdinalIgnoreCase));
        }

    }
}
