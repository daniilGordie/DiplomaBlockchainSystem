using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
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
        private readonly string _syncToken;

        public P2PNetworkService(ILogger<P2PNetworkService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _syncToken = configuration["P2P:SyncToken"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_syncToken))
            {
                _logger.LogWarning("[P2P] Sync token is not configured. Secure chain sync reads from peers will be rejected.");
            }
        }

        public void AddPeer(string url)
        {
            if (_peers.TryAdd(url, true))
            {
                _logger.LogInformation("[P2P] Added peer node: {PeerUrl}", url);
            }
        }

        public List<string> GetPeers()
        {
            return new List<string>(_peers.Keys);
        }

        public async Task BroadcastBlockAsync(Core.Block block)
        {
            if (_peers.IsEmpty) return;

            var blockModel = ToBlockModel(block);
            var tasks = _peers.Keys.Select(peerUrl => BroadcastToPeerAsync(peerUrl, blockModel));

            await Task.WhenAll(tasks);
        }

        public async Task<List<BlockModel>> FetchChainAsync(string peerUrl, string channelId)
        {
            var client = CreateClient(peerUrl);
            var response = await client.GetChainAsync(new ChainRequest
            {
                Count = int.MaxValue,
                ChannelId = string.IsNullOrWhiteSpace(channelId) ? "System" : channelId,
                UserName = NodeSyncUser,
                SyncToken = _syncToken
            });

            return response.Blocks.ToList();
        }

        public async Task<List<string>> FetchKnownChannelsAsync(string peerUrl)
        {
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
            var channel = _channels.GetOrAdd(peerUrl, url =>
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
