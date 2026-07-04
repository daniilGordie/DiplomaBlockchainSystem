using Blockchain.Core;
using Blockchain.Node.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Blockchain.Tests;

public sealed class PeerChainSyncFinalityTests
{
    [Fact]
    public async Task SyncFromPeerAsync_ShouldSkipBlockAdoptionWhenP2PFinalityIsDisabled()
    {
        var peerStore = new FakePeerStore();
        var service = new PeerChainSyncService(
            p2pService: null!,
            adoptPeerChain: null!,
            blockchainManager: null!,
            chainReader: null!,
            peerStore,
            Options.Create(new ConsensusOptions { AcceptP2PBlocksAsFinal = false }),
            NullLogger<PeerChainSyncService>.Instance);

        var result = await service.SyncFromPeerAsync("https://peer.example.test");

        Assert.Equal(0, result.AcceptedBlocks);
        Assert.Empty(result.ChangedChannels);
        Assert.Contains("https://peer.example.test", peerStore.SavedPeers);
        Assert.Empty(peerStore.FailedPeers);
    }

    private sealed class FakePeerStore : IPeerStore
    {
        public List<string> SavedPeers { get; } = new();
        public List<string> FailedPeers { get; } = new();

        public List<PeerInfo> LoadPeerInfos() => new();
        public List<string> LoadPeers() => SavedPeers.ToList();
        public void SavePeer(PeerInfo peer) => SavedPeers.Add(peer.Url);
        public void SavePeer(string url) => SavedPeers.Add(url);
        public void MarkPeerSeen(string url) { }
        public void MarkPeerFailure(string url) => FailedPeers.Add(url);
    }
}
