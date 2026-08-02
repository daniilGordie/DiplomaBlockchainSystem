using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Blockchain.Node.Services
{
    public class P2PNetworkService
    {
        private readonly ConcurrentDictionary<string, bool> _peers = new();
        private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
        private readonly ILogger<P2PNetworkService> _logger;
        private readonly P2POptions _options;
        private readonly IrohSidecarClient _irohSidecarClient;
        private readonly NodeIdentity _nodeIdentity;

        public P2PNetworkService(
            ILogger<P2PNetworkService> logger,
            IOptions<P2POptions> options,
            IrohSidecarClient irohSidecarClient,
            NodeIdentity nodeIdentity)
        {
            _logger = logger;
            _options = options.Value;
            _irohSidecarClient = irohSidecarClient;
            _nodeIdentity = nodeIdentity;
        }

        public void AddPeer(string url)
        {
            string normalizedUrl = P2POptions.NormalizeUrl(url);
            if (string.IsNullOrWhiteSpace(normalizedUrl) || IsSelf(normalizedUrl))
            {
                return;
            }

            if (_peers.TryAdd(normalizedUrl, true))
            {
                _logger.LogInformation("[P2P] Added peer node: {PeerUrl}", normalizedUrl);
            }
        }

        public void RemovePeer(string url)
        {
            string normalizedUrl = P2POptions.NormalizeUrl(url);
            if (string.IsNullOrWhiteSpace(normalizedUrl))
            {
                return;
            }

            if (_peers.TryRemove(normalizedUrl, out _))
            {
                _logger.LogInformation("[P2P] Removed peer node: {PeerUrl}", normalizedUrl);
            }

            if (_channels.TryRemove(normalizedUrl, out var channel))
            {
                channel.Dispose();
            }
        }

        public async Task<StatusReply> RegisterWithBootstrapAsync(string bootstrapUrl)
        {
            var client = CreateClient(bootstrapUrl);
            string publicUrl = _options.NormalizedPublicUrl;
            if (_options.Iroh.Enabled)
            {
                string irohPublicUrl = await WaitForIrohPublicUrlAsync();
                if (!string.IsNullOrWhiteSpace(irohPublicUrl))
                {
                    publicUrl = irohPublicUrl;
                }
            }

            string role = _options.Role.ToString();
            var signed = _nodeIdentity.CreateRegistration(_options.EffectiveNodeId, publicUrl, role);

            return await client.RegisterPeerAsync(new RegisterPeerRequest
            {
                NodeId = _options.EffectiveNodeId,
                PublicUrl = publicUrl,
                Role = role,
                RegistrationToken = _options.AllowRegistrationTokenFallback ? _options.RegistrationToken : string.Empty,
                NodePublicKey = signed.NodePublicKey,
                Signature = signed.Signature,
                SignedAt = signed.SignedAt,
                Nonce = signed.Nonce
            });
        }

        private async Task<string> WaitForIrohPublicUrlAsync()
        {
            for (int attempt = 1; attempt <= 30; attempt++)
            {
                string publicUrl = await _irohSidecarClient.GetPublicUrlAsync();
                if (!string.IsNullOrWhiteSpace(publicUrl))
                {
                    return publicUrl;
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            return string.Empty;
        }

        public async Task<List<PeerDirectoryItem>> FetchPeerDirectoryAsync(string peerUrl)
        {
            var client = CreateClient(peerUrl);
            var response = await client.GetPeerDirectoryAsync(new EmptyRequest());

            return response.Peers
                .Where(peer => !string.IsNullOrWhiteSpace(peer.PublicUrl))
                .Where(peer => !IsSelf(peer.PublicUrl))
                .GroupBy(peer => P2POptions.NormalizeUrl(peer.PublicUrl), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        public async Task<StatusReply> SubmitBlockAsync(string peerUrl, BlockModel block)
        {
            var client = CreateClient(peerUrl);
            return await client.ReceiveBlockAsync(block);
        }

        private BlockchainService.BlockchainServiceClient CreateClient(string peerUrl)
        {
            string normalizedUrl = P2POptions.NormalizeUrl(peerUrl);
            var channel = _channels.GetOrAdd(normalizedUrl, url =>
            {
                var handler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    PooledConnectionIdleTimeout = System.Threading.Timeout.InfiniteTimeSpan,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
                };

                return GrpcChannel.ForAddress(url, new GrpcChannelOptions { HttpHandler = handler });
            });

            return new BlockchainService.BlockchainServiceClient(channel);
        }

        private bool IsSelf(string peerUrl)
        {
            string publicUrl = _options.NormalizedPublicUrl;
            return !string.IsNullOrWhiteSpace(publicUrl)
                && string.Equals(publicUrl, P2POptions.NormalizeUrl(peerUrl), StringComparison.OrdinalIgnoreCase);
        }

    }
}
