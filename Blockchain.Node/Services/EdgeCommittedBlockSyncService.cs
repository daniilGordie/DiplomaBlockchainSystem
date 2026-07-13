using Blockchain.Application.Blocks;
using Blockchain.Core;
using Blockchain.Core.Consensus;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

public sealed class EdgeCommittedBlockSyncService : BackgroundService
{
    private readonly NexusNodeOptions _nodeOptions;
    private readonly P2POptions _p2pOptions;
    private readonly ConsensusOptions _consensusOptions;
    private readonly string _networkBootstrapHttpUrl;
    private readonly IPeerStore _peerStore;
    private readonly IChainReader _chainReader;
    private readonly IrohSidecarClient _irohSidecar;
    private readonly CommittedBlockApplier _committedBlockApplier;
    private readonly ILogger<EdgeCommittedBlockSyncService> _logger;
    private readonly object _statusLock = new();
    private EdgeCommittedBlockSyncStatus _status = EdgeCommittedBlockSyncStatus.NotStarted;

    public EdgeCommittedBlockSyncService(
        IOptions<NexusNodeOptions> nodeOptions,
        IOptions<P2POptions> p2pOptions,
        IOptions<ConsensusOptions> consensusOptions,
        IConfiguration configuration,
        IPeerStore peerStore,
        IChainReader chainReader,
        IrohSidecarClient irohSidecar,
        CommittedBlockApplier committedBlockApplier,
        ILogger<EdgeCommittedBlockSyncService> logger)
    {
        _nodeOptions = nodeOptions.Value;
        _p2pOptions = p2pOptions.Value;
        _consensusOptions = consensusOptions.Value;
        _networkBootstrapHttpUrl = P2POptions.NormalizeUrl(configuration["Network:BootstrapHttpUrl"]);
        _peerStore = peerStore;
        _chainReader = chainReader;
        _irohSidecar = irohSidecar;
        _committedBlockApplier = committedBlockApplier;
        _logger = logger;
    }

    public EdgeCommittedBlockSyncStatus GetStatus()
    {
        lock (_statusLock)
        {
            return _status;
        }
    }

    public async Task<EdgeCommittedBlockSyncStatus> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_nodeOptions.IsEdge)
        {
            return UpdateStatus("disabled", 0, 0, string.Empty);
        }

        if (!_irohSidecar.Enabled)
        {
            return UpdateStatus("error", 0, 0, "Iroh sidecar is disabled.");
        }

        var peers = GetCandidatePeers()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (peers.Length == 0)
        {
            return UpdateStatus("waiting_for_peers", 0, 0, "No consensus/bootstrap peers are known yet.");
        }

        int applied = 0;
        int failures = 0;
        string lastPeer = string.Empty;
        var changedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var peer in peers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            lastPeer = peer;
            try
            {
                var remoteChannels = IrohSidecarClient.IsIrohPeerUrl(peer)
                    ? await _irohSidecar.FetchKnownChannelsAsync(peer, cancellationToken)
                    : await _irohSidecar.FetchKnownChannelsOverHttpAsync(peer, cancellationToken);
                var channels = remoteChannels
                    .Concat(_chainReader.GetKnownChannels())
                    .Select(ChannelName.Normalize)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (!channels.Contains("System", StringComparer.OrdinalIgnoreCase))
                {
                    channels = new[] { "System" }.Concat(channels).ToArray();
                }

                foreach (var channelId in channels.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    applied += await SyncChannelAsync(peer, channelId, changedChannels, cancellationToken);
                }

                _peerStore.MarkPeerSeen(peer);
            }
            catch (Exception ex)
            {
                failures++;
                _peerStore.MarkPeerFailure(peer);
                _logger.LogWarning(ex, "[EdgeSync] Sync from {Peer} failed.", peer);
            }
        }

        string state = failures == peers.Length ? "error" : applied > 0 ? "synced_with_changes" : "synced";
        string error = failures == peers.Length ? "All sync peers failed." : string.Empty;
        return UpdateStatus(state, applied, changedChannels.Count, error, lastPeer);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_nodeOptions.IsEdge)
        {
            UpdateStatus("disabled", 0, 0, string.Empty);
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        int intervalSeconds = Math.Clamp(_p2pOptions.DiscoveryIntervalSeconds, 10, 3600);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        do
        {
            await SyncOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<int> SyncChannelAsync(
        string peer,
        string channelId,
        ISet<string> changedChannels,
        CancellationToken cancellationToken)
    {
        string safeChannelId = ChannelName.Normalize(channelId);
        var latest = _chainReader.GetLatestBlock(safeChannelId);
        int afterIndex = latest?.Index ?? -1;
        string afterHash = latest?.Hash ?? string.Empty;
        var envelopes = IrohSidecarClient.IsIrohPeerUrl(peer)
            ? await _irohSidecar.FetchCommittedSinceAsync(peer, safeChannelId, afterIndex, afterHash, cancellationToken)
            : await _irohSidecar.FetchCommittedSinceOverHttpAsync(peer, safeChannelId, afterIndex, afterHash, cancellationToken);
        int applied = 0;

        foreach (var envelope in envelopes.OrderBy(item => item.Block.Index))
        {
            var block = GrpcBlockProcessor.ToBlock(envelope.Block);
            var metadata = ToFinalityMetadata(envelope.Finality);
            var validation = ValidateEnvelope(block, envelope, metadata);
            if (!validation.Success)
            {
                _logger.LogWarning("[EdgeSync] Rejected committed block {Hash}: {Message}", block.Hash, validation.Message);
                continue;
            }

            // Edge catch-up applies blocks that have already been finalized by Raft.
            // Re-running producer proof locally is invalid while the Edge is behind:
            // the proof was created against the consensus node's pre-commit state,
            // which can differ from the Edge's partially synchronized view.

            var result = await _committedBlockApplier.ApplyAsync(
                block,
                envelope.Block,
                broadcastToPeers: false,
                finalityMetadata: metadata);
            if (!result.Success)
            {
                _logger.LogWarning("[EdgeSync] Failed to apply committed block {Hash}: {Message}", block.Hash, result.Message);
                continue;
            }

            applied++;
            changedChannels.Add(block.ChannelId);
        }

        return applied;
    }

    private static BlockWriteResult ValidateEnvelope(
        Block block,
        IrohCommittedBlockEnvelope envelope,
        BlockFinalityMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.FinalityMode))
        {
            return new BlockWriteResult(false, "missing finality mode", block.ChannelId);
        }

        if (!string.Equals(block.Hash, metadata.BlockHash, StringComparison.OrdinalIgnoreCase))
        {
            return new BlockWriteResult(false, "finality metadata block hash mismatch", block.ChannelId);
        }

        if (!string.Equals(block.ChannelId, metadata.ChannelId, StringComparison.OrdinalIgnoreCase))
        {
            return new BlockWriteResult(false, "finality metadata channel mismatch", block.ChannelId);
        }

        if (envelope.Block.ContributionProof == null)
        {
            return new BlockWriteResult(false, "missing contribution proof", block.ChannelId);
        }

        return new BlockWriteResult(true, "accepted", block.ChannelId);
    }

    private IEnumerable<string> GetCandidatePeers()
    {
        if (!string.IsNullOrWhiteSpace(_networkBootstrapHttpUrl))
        {
            yield return _networkBootstrapHttpUrl;
        }

        var grpcBootstrapPeers = new HashSet<string>(
            _p2pOptions.NormalizedBootstrapPeers.Select(P2POptions.NormalizeUrl),
            StringComparer.OrdinalIgnoreCase);

        foreach (var peer in _peerStore.LoadPeerInfos())
        {
            string normalized = P2POptions.NormalizeUrl(peer.Url);
            if (grpcBootstrapPeers.Contains(normalized) && !IrohSidecarClient.IsIrohPeerUrl(peer.Url))
            {
                continue;
            }

            yield return peer.Url;
        }
    }

    private EdgeCommittedBlockSyncStatus UpdateStatus(
        string state,
        int appliedBlocks,
        int changedChannels,
        string lastError,
        string lastPeer = "")
    {
        var status = new EdgeCommittedBlockSyncStatus(
            _nodeOptions.IsEdge,
            state,
            appliedBlocks,
            changedChannels,
            DateTime.UtcNow,
            lastPeer,
            lastError);

        lock (_statusLock)
        {
            _status = status;
        }

        return status;
    }

    private static BlockFinalityMetadata ToFinalityMetadata(IrohFinalityMetadataModel metadata) =>
        new(
            metadata.BlockHash,
            metadata.ChannelId,
            metadata.FinalityMode,
            metadata.RaftLogIndex,
            metadata.RaftTerm,
            metadata.CommittedAtUtc);
}

public sealed record EdgeCommittedBlockSyncStatus(
    bool Enabled,
    string State,
    int LastAppliedBlocks,
    int LastChangedChannels,
    DateTime? LastSyncUtc,
    string LastPeer,
    string LastError)
{
    public static EdgeCommittedBlockSyncStatus NotStarted { get; } = new(
        false,
        "not_started",
        0,
        0,
        null,
        string.Empty,
        string.Empty);
}
