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
        public const string NodeSyncUser = "__P2P_SYNC__";

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
            if (string.IsNullOrWhiteSpace(_options.SyncToken))
            {
                _logger.LogWarning("[P2P] Sync token is not configured. Secure chain sync reads from peers will be rejected.");
            }
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

        public List<string> GetPeers()
        {
            return new List<string>(_peers.Keys);
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

        public async Task BroadcastBlockAsync(Core.Block block)
        {
            if (_peers.IsEmpty) return;

            var blockModel = ToBlockModel(block);
            var irohPeers = _peers.Keys
                .Where(IrohSidecarClient.IsIrohPeerUrl)
                .ToArray();
            var grpcPeers = _peers.Keys
                .Where(peerUrl => !IrohSidecarClient.IsIrohPeerUrl(peerUrl))
                .ToArray();

            var tasks = grpcPeers.Select(peerUrl => BroadcastToPeerAsync(peerUrl, blockModel)).ToList();
            if (irohPeers.Length > 0)
            {
                tasks.Add(_irohSidecarClient.BroadcastBlockAsync(irohPeers, block));
            }

            await Task.WhenAll(tasks);
        }

        public async Task<List<BlockModel>> FetchChainAsync(string peerUrl, string channelId)
        {
            if (IrohSidecarClient.IsIrohPeerUrl(peerUrl))
            {
                return (await _irohSidecarClient.FetchChainAsync(peerUrl, channelId)).ToList();
            }

            var client = CreateClient(peerUrl);
            var blocks = new List<BlockModel>();
            int afterIndex = -1;
            const int pageSize = 500;

            while (true)
            {
                var response = await client.GetChainAsync(new ChainRequest
                {
                    Count = pageSize,
                    ChannelId = string.IsNullOrWhiteSpace(channelId) ? "System" : channelId,
                    UserName = NodeSyncUser,
                    SyncToken = _options.SyncToken,
                    AfterIndex = afterIndex
                });

                if (response.Blocks.Count == 0)
                {
                    break;
                }

                blocks.AddRange(response.Blocks);
                afterIndex = response.Blocks.Max(block => block.Index);

                if (response.Blocks.Count < pageSize)
                {
                    break;
                }
            }

            return blocks;
        }

        public async Task<List<string>> FetchKnownChannelsAsync(string peerUrl)
        {
            if (IrohSidecarClient.IsIrohPeerUrl(peerUrl))
            {
                return (await _irohSidecarClient.FetchKnownChannelsAsync(peerUrl)).ToList();
            }

            var client = CreateClient(peerUrl);
            var response = await client.GetKnownChannelsAsync(new EmptyRequest());

            return response.ChannelIds
                .Where(channel => !string.IsNullOrWhiteSpace(channel))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task BroadcastToPeerAsync(string peerUrl, BlockModel block)
        {
            try
            {
                var client = CreateClient(peerUrl);
                var response = await client.BroadcastBlockAsync(block);
                if (response.Success)
                {
                    _logger.LogInformation("[P2P] Broadcasted block {BlockHash} ({ChannelId}#{Index}) to {PeerUrl}.",
                        block.Hash, block.ChannelId, block.Index, peerUrl);
                }
                else
                {
                    _logger.LogWarning("[P2P] Peer {PeerUrl} rejected block {BlockHash}: {Message}",
                        peerUrl, block.Hash, response.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[P2P] Failed to reach peer {PeerUrl}: {Message}", peerUrl, ex.Message);
            }
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

        private static BlockModel ToBlockModel(Core.Block block)
        {
            return new BlockModel
            {
                Index = block.Index,
                Data = block.Data,
                PreviousHash = block.PreviousHash,
                Hash = block.Hash,
                Timestamp = block.Timestamp.ToString("O"),
                ValidatorPublicKey = block.ValidatorPublicKey ?? "",
                Signature = block.Signature ?? "",
                Nonce = block.Nonce,
                ChannelId = string.IsNullOrWhiteSpace(block.ChannelId) ? "System" : block.ChannelId
            };
        }
    }
}
