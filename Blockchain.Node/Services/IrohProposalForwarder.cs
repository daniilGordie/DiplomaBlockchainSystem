using Blockchain.Application.Blocks;
using Blockchain.Core;
using Blockchain.Core.Consensus;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

public sealed class IrohProposalForwarder
{
    private readonly IPeerStore _peerStore;
    private readonly IrohSidecarClient _irohSidecar;
    private readonly P2PNetworkService _p2pNetwork;
    private readonly P2POptions _p2pOptions;
    private readonly string _networkBootstrapHttpUrl;
    private readonly ILogger<IrohProposalForwarder> _logger;
    private readonly object _statusLock = new();
    private EdgeProposalForwardingStatus _status = EdgeProposalForwardingStatus.NotStarted;
    private int _pendingCount;

    public IrohProposalForwarder(
        IPeerStore peerStore,
        IrohSidecarClient irohSidecar,
        P2PNetworkService p2pNetwork,
        IOptions<P2POptions> p2pOptions,
        IConfiguration configuration,
        ILogger<IrohProposalForwarder> logger)
    {
        _peerStore = peerStore;
        _irohSidecar = irohSidecar;
        _p2pNetwork = p2pNetwork;
        _p2pOptions = p2pOptions.Value;
        _networkBootstrapHttpUrl = P2POptions.NormalizeUrl(configuration["Network:BootstrapHttpUrl"]);
        _logger = logger;
    }

    public async Task<BlockWriteResult> ForwardAsync(BlockProposal proposal, BlockModel sourceModel, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _pendingCount);
        var model = EnsureContributionProof(sourceModel, proposal);
        try
        {
            var peers = GetCandidatePeers()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (peers.Length == 0)
            {
                return CompleteFailure(
                    proposal.Block.ChannelId,
                    "No consensus/bootstrap peers are known yet. Wait for bootstrap discovery or import a connection invite.",
                    string.Empty);
            }

            var failures = new List<string>();
            foreach (var peer in peers)
            {
                try
                {
                    if (IrohSidecarClient.IsIrohPeerUrl(peer))
                    {
                        if (!_irohSidecar.Enabled)
                        {
                            failures.Add($"{peer}: Iroh sidecar is disabled.");
                            continue;
                        }

                        var response = await _irohSidecar.SubmitBlockAsync(peer, model, cancellationToken);
                        if (response.Success)
                        {
                            _peerStore.MarkPeerSeen(peer);
                            return CompleteSuccess(response.ChannelId, response.Message, peer);
                        }

                        failures.Add($"{peer}: {response.Message}");
                        _peerStore.MarkPeerFailure(peer);
                    }
                    else
                    {
                        var response = await _p2pNetwork.SubmitBlockAsync(peer, model);
                        if (response.Success)
                        {
                            _peerStore.MarkPeerSeen(peer);
                            return CompleteSuccess(proposal.Block.ChannelId, response.Message, peer);
                        }

                        failures.Add($"{peer}: {response.Message}");
                        _peerStore.MarkPeerFailure(peer);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{peer}: {ex.Message}");
                    _peerStore.MarkPeerFailure(peer);
                    _logger.LogWarning(ex, "[Iroh] Proposal forwarding to {Peer} failed.", peer);
                }
            }

            string detail = failures.Count == 0 ? "no forwarding attempts were made" : string.Join("; ", failures);
            return CompleteFailure(proposal.Block.ChannelId, $"Consensus forwarding failed: {detail}", peers.LastOrDefault() ?? string.Empty);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    public EdgeProposalForwardingStatus GetStatus()
    {
        lock (_statusLock)
        {
            return _status with { PendingCount = Math.Max(0, Volatile.Read(ref _pendingCount)) };
        }
    }

    private BlockWriteResult CompleteSuccess(string channelId, string message, string peer)
    {
        UpdateStatus("forwarded", channelId, message, peer, true);
        return new BlockWriteResult(true, message, channelId);
    }

    private BlockWriteResult CompleteFailure(string channelId, string message, string peer)
    {
        UpdateStatus("failed", channelId, message, peer, false);
        return new BlockWriteResult(false, message, channelId);
    }

    private void UpdateStatus(string state, string channelId, string message, string peer, bool success)
    {
        lock (_statusLock)
        {
            _status = new EdgeProposalForwardingStatus(
                true,
                Math.Max(0, Volatile.Read(ref _pendingCount)),
                state,
                channelId,
                peer,
                message,
                DateTime.UtcNow,
                success ? _status.SuccessCount + 1 : _status.SuccessCount,
                success ? _status.FailureCount : _status.FailureCount + 1);
        }
    }

    private IEnumerable<string> GetCandidatePeers()
    {
        foreach (var peerUrl in _p2pOptions.NormalizedBootstrapPeers)
        {
            yield return peerUrl;
        }

        foreach (var peer in _peerStore.LoadPeerInfos())
        {
            if (!string.IsNullOrWhiteSpace(_networkBootstrapHttpUrl) &&
                string.Equals(P2POptions.NormalizeUrl(peer.Url), _networkBootstrapHttpUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return peer.Url;
        }
    }

    private static BlockModel EnsureContributionProof(BlockModel sourceModel, BlockProposal proposal)
    {
        sourceModel.ContributionProof ??= GrpcProjectMapper.ToContributionProofModel(proposal.ContributionProof);
        return sourceModel;
    }
}

public sealed record EdgeProposalForwardingStatus(
    bool Enabled,
    int PendingCount,
    string State,
    string LastChannelId,
    string LastPeer,
    string LastMessage,
    DateTime? LastAttemptUtc,
    long SuccessCount,
    long FailureCount)
{
    public static EdgeProposalForwardingStatus NotStarted { get; } = new(
        false,
        0,
        "not_started",
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        0,
        0);
}
