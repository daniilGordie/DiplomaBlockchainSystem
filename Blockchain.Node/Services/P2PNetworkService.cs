using Grpc.Net.Client;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;

namespace Blockchain.Node.Services
{
    public class P2PNetworkService
    {
        private readonly ConcurrentDictionary<string, bool> _peers = new();

        private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
        private readonly ILogger<P2PNetworkService> _logger;

        public P2PNetworkService(ILogger<P2PNetworkService> logger)
        {
            _logger = logger;
        }

        public void AddPeer(string url)
        {
            if (_peers.TryAdd(url, true))
            {
                _logger.LogInformation($"[P2P] 🌐 Added new peer node: {url}");
            }
        }

        public List<string> GetPeers()
        {
            return new List<string>(_peers.Keys);
        }

        public async Task BroadcastBlockAsync(Core.Block block)
        {
            if (_peers.IsEmpty) return;

            var blockModel = new BlockModel
            {
                Index = block.Index,
                Data = block.Data,
                PreviousHash = block.PreviousHash,
                Hash = block.Hash,
                Timestamp = block.Timestamp.ToString("O"),
                ValidatorPublicKey = block.ValidatorPublicKey ?? "",
                Signature = block.Signature ?? "",
                Nonce = block.Nonce
            };

            foreach (var peerUrl in _peers.Keys)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var channel = _channels.GetOrAdd(peerUrl, url =>
                        {
                            var handler = new SocketsHttpHandler
                            {
                                EnableMultipleHttp2Connections = true, 
                                PooledConnectionIdleTimeout = System.Threading.Timeout.InfiniteTimeSpan,
                                KeepAlivePingDelay = System.TimeSpan.FromSeconds(60),
                                KeepAlivePingTimeout = System.TimeSpan.FromSeconds(30)
                            };
                            return GrpcChannel.ForAddress(url, new GrpcChannelOptions { HttpHandler = handler });
                        });

                        var client = new BlockchainService.BlockchainServiceClient(channel);
                        await client.BroadcastBlockAsync(blockModel);

                        _logger.LogInformation($"[P2P] 📤 Broadcasted Block #{block.Index} to {peerUrl}");
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogWarning($"[P2P] ❌ Failed to reach peer {peerUrl}: {ex.Message}");
                    }
                });
            }
        }
    }
}