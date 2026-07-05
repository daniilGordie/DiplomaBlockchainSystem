using Blockchain.Core;
using Blockchain.Core.Consensus;
using Blockchain.Node.Services;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Endpoints;

public static class ConsensusEndpoints
{
    public static IEndpointRouteBuilder MapConsensusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/consensus/projects/{projectId}/producer", (
            string projectId,
            IChainReader chainReader,
            BlockProposalFactory proposalFactory,
            IOptions<ConsensusOptions> consensusOptions,
            IOptions<NexusNodeOptions> nodeOptions,
            IOptions<RaftOptions> raftOptions,
            IServiceProvider services) =>
        {
            string channelId = ChannelName.Normalize(projectId);
            var consensus = consensusOptions.Value;
            var node = nodeOptions.Value;
            var raft = raftOptions.Value;
            bool raftClusterRegistered = IsRaftClusterAvailable(consensus, node, raft, services);
            var raftStatus = BuildRaftStatus(consensus, node, raft, raftClusterRegistered);
            var latest = chainReader.GetLatestBlock(channelId);
            int nextIndex = latest != null ? latest.Index + 1 : 0;

            var probeBlock = new Block
            {
                Index = nextIndex,
                ChannelId = channelId,
                PreviousHash = latest?.Hash ?? "0"
            };

            var proposal = proposalFactory.BuildImplicitProposal(probeBlock);
            if (!proposal.Accepted || proposal.Proposal == null)
            {
                return Results.Json(new ConsensusProducerResponse(
                    channelId,
                    nextIndex,
                    consensus.EnableProofOfContributionValidation,
                    consensus.RequireProofOfWork,
                    consensus.AcceptP2PBlocksAsFinal,
                    consensus.FinalityMode,
                    raftStatus,
                    false,
                    proposal.Reason,
                    null));
            }

            var proof = proposal.Proposal.ContributionProof;
            return Results.Json(new ConsensusProducerResponse(
                channelId,
                nextIndex,
                consensus.EnableProofOfContributionValidation,
                consensus.RequireProofOfWork,
                consensus.AcceptP2PBlocksAsFinal,
                consensus.FinalityMode,
                raftStatus,
                true,
                "selected",
                new ContributionProofResponse(
                    proof.ProjectId,
                    proof.Epoch,
                    proof.ProducerPublicKey,
                    proof.ProducerScore,
                    proof.ScoreSnapshotHash,
                    proof.EvidenceBlockHashes.ToArray())));
        });

        endpoints.MapGet("/api/consensus/raft/status", (
            IOptions<ConsensusOptions> consensusOptions,
            IOptions<NexusNodeOptions> nodeOptions,
            IOptions<RaftOptions> raftOptions,
            IServiceProvider services) =>
        {
            var consensus = consensusOptions.Value;
            var node = nodeOptions.Value;
            var raft = raftOptions.Value;
            var cluster = TryGetRaftCluster(consensus, node, raft, services);
            bool clusterRegistered = cluster != null;
            var baseStatus = BuildRaftStatus(consensus, node, raft, clusterRegistered);

            return Results.Json(new RaftRuntimeStatusResponse(
                baseStatus,
                cluster?.Term,
                cluster != null && cluster.Readiness.IsCompletedSuccessfully,
                cluster != null && !cluster.LeadershipToken.IsCancellationRequested,
                cluster != null && !cluster.ConsensusToken.IsCancellationRequested,
                GetLeader(cluster),
                GetMembers(cluster)));
        });

        return endpoints;
    }

    private static RaftStatusResponse BuildRaftStatus(
        ConsensusOptions consensus,
        NexusNodeOptions node,
        RaftOptions raft,
        bool raftClusterRegistered)
    {
        bool raftFinality = string.Equals(consensus.FinalityMode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase);
        bool requested = raftFinality && node.IsConsensusMember;
        bool configured = node.IsConsensusMember && raft.HasMinimumConfiguration;
        bool ready = requested && configured && raftClusterRegistered;
        string status = raftFinality && node.IsEdge
            ? "edge_node_uses_remote_consensus"
            : !requested
            ? "not_requested"
            : !configured
                ? "missing_configuration"
                : !raftClusterRegistered
                    ? "cluster_not_registered"
                    : "ready";

        return new RaftStatusResponse(
            requested,
            configured,
            raftClusterRegistered,
            ready,
            status,
            raft.NodeId,
            raft.PublicEndPoint,
            raft.LogPath,
            raft.Peers.Select(peer => new RaftPeerResponse(peer.Id, peer.EndPoint)).ToArray(),
            node.EffectiveRole.ToString());
    }

    private static bool IsRaftClusterAvailable(
        ConsensusOptions consensus,
        NexusNodeOptions node,
        RaftOptions raft,
        IServiceProvider services)
    {
        bool requested = string.Equals(consensus.FinalityMode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase)
                         && node.IsConsensusMember;
        if (!requested || !raft.HasMinimumConfiguration)
        {
            return false;
        }

        try
        {
            return services.GetService<IRaftCluster>() != null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IRaftCluster? TryGetRaftCluster(
        ConsensusOptions consensus,
        NexusNodeOptions node,
        RaftOptions raft,
        IServiceProvider services)
    {
        if (!string.Equals(consensus.FinalityMode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase) ||
            !node.IsConsensusMember ||
            !raft.HasMinimumConfiguration)
        {
            return null;
        }

        try
        {
            return services.GetService<IRaftCluster>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? GetLeader(IRaftCluster? cluster)
    {
        if (cluster is not ICluster genericCluster)
        {
            return null;
        }

        return genericCluster.Leader?.ToString();
    }

    private static IReadOnlyList<RaftMemberRuntimeResponse> GetMembers(IRaftCluster? cluster)
    {
        if (cluster == null)
        {
            return Array.Empty<RaftMemberRuntimeResponse>();
        }

        return cluster.Members
            .Select(member => new RaftMemberRuntimeResponse(
                member.ToString() ?? string.Empty,
                member.IsLeader,
                member.IsRemote,
                member.Status.ToString()))
            .ToArray();
    }
}

public sealed record ConsensusProducerResponse(
    string ProjectId,
    int NextBlockIndex,
    bool ProofOfContributionValidationEnabled,
    bool RequireProofOfWork,
    bool AcceptP2PBlocksAsFinal,
    string FinalityMode,
    RaftStatusResponse Raft,
    bool HasEligibleProducer,
    string Status,
    ContributionProofResponse? ContributionProof);

public sealed record RaftStatusResponse(
    bool Requested,
    bool Configured,
    bool ClusterRegistered,
    bool Ready,
    string Status,
    string NodeId,
    string PublicEndPoint,
    string LogPath,
    IReadOnlyList<RaftPeerResponse> Peers,
    string NodeRole);

public sealed record RaftRuntimeStatusResponse(
    RaftStatusResponse Configuration,
    long? Term,
    bool ReadinessCompleted,
    bool LocalNodeIsLeader,
    bool HasLeaderConnection,
    string? Leader,
    IReadOnlyList<RaftMemberRuntimeResponse> Members);

public sealed record RaftPeerResponse(
    string Id,
    string EndPoint);

public sealed record RaftMemberRuntimeResponse(
    string Endpoint,
    bool IsLeader,
    bool IsRemote,
    string Status);

public sealed record ContributionProofResponse(
    string ProjectId,
    long Epoch,
    string ProducerPublicKey,
    int ProducerScore,
    string ScoreSnapshotHash,
    IReadOnlyList<string> EvidenceBlockHashes);
