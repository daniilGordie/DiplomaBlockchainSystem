using Blockchain.Application.Blocks;
using Blockchain.Core;
using Blockchain.Core.Consensus;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

public sealed class IrohProposalForwarder
{
    private readonly IPeerStore _peerStore;
    private readonly IrohSidecarClient _irohSidecar;
    private readonly P2POptions _p2pOptions;
    private readonly ILogger<IrohProposalForwarder> _logger;

    public IrohProposalForwarder(
        IPeerStore peerStore,
        IrohSidecarClient irohSidecar,
        IOptions<P2POptions> p2pOptions,
        ILogger<IrohProposalForwarder> logger)
    {
        _peerStore = peerStore;
        _irohSidecar = irohSidecar;
        _p2pOptions = p2pOptions.Value;
        _logger = logger;
    }

    public async Task<BlockWriteResult> ForwardAsync(BlockProposal proposal, BlockModel sourceModel, CancellationToken cancellationToken = default)
    {
        if (!_irohSidecar.Enabled)
        {
            return new BlockWriteResult(false, "Iroh sidecar is not enabled; Edge node cannot reach consensus core.", proposal.Block.ChannelId);
        }

        var model = EnsureContributionProof(sourceModel, proposal);
        var peers = GetCandidatePeers()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (peers.Length == 0)
        {
            return new BlockWriteResult(false, "No Iroh consensus/bootstrap peers are known yet. Wait for bootstrap discovery or import a connection invite.", proposal.Block.ChannelId);
        }

        var failures = new List<string>();
        foreach (var peer in peers)
        {
            try
            {
                var response = await _irohSidecar.SubmitBlockAsync(peer, model, cancellationToken);
                if (response.Success)
                {
                    _peerStore.MarkPeerSeen(peer);
                    return new BlockWriteResult(true, response.Message, response.ChannelId);
                }

                failures.Add($"{peer}: {response.Message}");
                _peerStore.MarkPeerFailure(peer);
            }
            catch (Exception ex)
            {
                failures.Add($"{peer}: {ex.Message}");
                _peerStore.MarkPeerFailure(peer);
                _logger.LogWarning(ex, "[Iroh] Proposal forwarding to {Peer} failed.", peer);
            }
        }

        string detail = failures.Count == 0 ? "no forwarding attempts were made" : string.Join("; ", failures);
        return new BlockWriteResult(false, $"Consensus forwarding failed: {detail}", proposal.Block.ChannelId);
    }

    private IEnumerable<string> GetCandidatePeers()
    {
        foreach (var peer in _peerStore.LoadPeerInfos())
        {
            if (IrohSidecarClient.IsIrohPeerUrl(peer.Url))
            {
                yield return peer.Url;
            }
        }

        foreach (var peerUrl in _p2pOptions.NormalizedBootstrapPeers)
        {
            if (IrohSidecarClient.IsIrohPeerUrl(peerUrl))
            {
                yield return peerUrl;
            }
        }
    }

    private static BlockModel EnsureContributionProof(BlockModel sourceModel, BlockProposal proposal)
    {
        sourceModel.ContributionProof ??= GrpcProjectMapper.ToContributionProofModel(proposal.ContributionProof);
        return sourceModel;
    }
}
