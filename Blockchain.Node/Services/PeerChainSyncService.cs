using Blockchain.Application.Blocks;
using Blockchain.Core;

namespace Blockchain.Node.Services;

public sealed class PeerChainSyncService
{
    private readonly P2PNetworkService _p2pService;
    private readonly AdoptPeerChainUseCase _adoptPeerChain;
    private readonly BlockchainManager _blockchainManager;
    private readonly IChainReader _chainReader;
    private readonly IPeerStore _peerStore;
    private readonly ILogger<PeerChainSyncService> _logger;

    public PeerChainSyncService(
        P2PNetworkService p2pService,
        AdoptPeerChainUseCase adoptPeerChain,
        BlockchainManager blockchainManager,
        IChainReader chainReader,
        IPeerStore peerStore,
        ILogger<PeerChainSyncService> logger)
    {
        _p2pService = p2pService;
        _adoptPeerChain = adoptPeerChain;
        _blockchainManager = blockchainManager;
        _chainReader = chainReader;
        _peerStore = peerStore;
        _logger = logger;
    }

    public async Task<PeerChainSyncResult> SyncFromPeerAsync(string peerUrl)
    {
        int acceptedBlocks = 0;
        var changedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                var candidateChain = blocks
                    .Select(GrpcBlockProcessor.ToBlock)
                    .OrderBy(block => block.Index)
                    .ToList();

                if (_adoptPeerChain.Execute(channelId, candidateChain))
                {
                    acceptedBlocks += candidateChain.Count;
                    changedChannels.Add(channelId);
                    continue;
                }

                foreach (var block in candidateChain)
                {
                    if (_chainReader.BlockExists(block.Hash, block.ChannelId))
                    {
                        continue;
                    }

                    if (_blockchainManager.ProcessPeerBlock(block))
                    {
                        acceptedBlocks++;
                        changedChannels.Add(block.ChannelId);
                    }
                }
            }

            _peerStore.SavePeer(peerUrl);
        }
        catch (Exception ex)
        {
            _peerStore.MarkPeerFailure(peerUrl);
            _logger.LogWarning("[P2P] Sync from {PeerUrl} failed: {Message}", peerUrl, ex.Message);
        }

        return new PeerChainSyncResult(acceptedBlocks, changedChannels.ToArray());
    }
}

public sealed record PeerChainSyncResult(int AcceptedBlocks, IReadOnlyList<string> ChangedChannels);
