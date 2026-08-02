using Blockchain.Core;
using Blockchain.Core.Consensus;
using Blockchain.Node.Services;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace Blockchain.Node.Endpoints;

#pragma warning disable DOTNEXT001

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

        endpoints.MapGet("/api/consensus/raft/status", async (
            IOptions<ConsensusOptions> consensusOptions,
            IOptions<NexusNodeOptions> nodeOptions,
            IOptions<RaftOptions> raftOptions,
            IrohSidecarClient irohSidecar,
            IServiceProvider services) =>
        {
            var consensus = consensusOptions.Value;
            var node = nodeOptions.Value;
            var raft = raftOptions.Value;
            var irohStatus = raft.UsesIrohTransport ? await irohSidecar.GetStatusAsync() : null;
            var cluster = TryGetRaftCluster(consensus, node, raft, services);
            bool clusterRegistered = cluster != null;
            var baseStatus = BuildRaftStatus(consensus, node, raft, clusterRegistered);
            var stateMachine = services.GetService<RaftBlockStateMachine>()?.GetDiagnostics();
            var wal = cluster?.AuditTrail as DotNext.Net.Cluster.Consensus.Raft.StateMachine.WriteAheadLog;
            long? lastCommittedIndex = wal?.LastCommittedEntryIndex;
            long? lastAppliedIndex = wal?.LastAppliedIndex;
            string? leader = GetLeader(cluster);
            bool readinessCompleted = cluster != null && cluster.Readiness.IsCompletedSuccessfully;
            bool operational = baseStatus.Ready &&
                               readinessCompleted &&
                               !string.IsNullOrWhiteSpace(leader) &&
                               stateMachine?.StateMachineHealthy == true &&
                               lastCommittedIndex == lastAppliedIndex;
            string operationalStatus = !baseStatus.Ready
                ? baseStatus.Status
                : !readinessCompleted
                    ? "cluster_starting"
                    : string.IsNullOrWhiteSpace(leader)
                        ? "leader_unavailable"
                        : stateMachine?.StateMachineHealthy != true
                            ? "state_machine_faulted"
                            : lastCommittedIndex != lastAppliedIndex
                                ? "state_machine_catching_up"
                                : "operational";

            return Results.Json(new RaftRuntimeStatusResponse(
                baseStatus,
                cluster?.Term,
                readinessCompleted,
                cluster != null && !cluster.LeadershipToken.IsCancellationRequested,
                cluster != null && !cluster.ConsensusToken.IsCancellationRequested,
                operational,
                operationalStatus,
                lastCommittedIndex,
                lastAppliedIndex,
                leader,
                GetMembers(cluster),
                stateMachine,
                new RaftTransportProofResponse(
                    raft.UsesIrohTransport ? "Iroh" : "Tcp",
                    raft.UsesIrohTransport ? "Iroh" : "Tcp",
                    irohStatus?.NodeId ?? string.Empty,
                    irohStatus?.ConnectionPath ?? string.Empty,
                    irohStatus?.TransportMode ?? string.Empty,
                    irohStatus?.DirectAddresses?.Length ?? 0,
                    raft.UsesIrohTransport ? "nexus/raft/1" : string.Empty)));
        });

        endpoints.MapPost("/api/consensus/raft/snapshot", async (
            RaftSnapshotTriggerRequest request,
            IOptions<ConsensusOptions> consensusOptions,
            IOptions<NexusNodeOptions> nodeOptions,
            IOptions<RaftOptions> raftOptions,
            IConfiguration configuration,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            if (!IsValidAdminToken(configuration, request.AdminToken))
            {
                return Results.Unauthorized();
            }

            var consensus = consensusOptions.Value;
            var node = nodeOptions.Value;
            var raft = raftOptions.Value;
            if (node.IsEdge)
            {
                return Results.BadRequest(new RaftSnapshotTriggerResponse(false, "Edge nodes do not run local Raft snapshots.", null));
            }

            var cluster = TryGetRaftCluster(consensus, node, raft, services);
            if (cluster == null)
            {
                return Results.BadRequest(new RaftSnapshotTriggerResponse(false, "Raft cluster is not available.", null));
            }

            if (cluster.AuditTrail is not DotNext.Net.Cluster.Consensus.Raft.StateMachine.WriteAheadLog wal)
            {
                return Results.BadRequest(new RaftSnapshotTriggerResponse(false, "Raft audit trail does not support snapshot flush.", null));
            }

            await cluster.ApplyReadBarrierAsync(cancellationToken);
            await wal.FlushAsync(cancellationToken);

            var diagnostics = services.GetService<RaftBlockStateMachine>()?.GetDiagnostics();
            return Results.Json(new RaftSnapshotTriggerResponse(
                diagnostics?.PublishedSnapshotIndex != null,
                diagnostics?.PublishedSnapshotIndex != null
                    ? "Published snapshot checkpoint flushed."
                    : diagnostics?.CurrentSnapshotIndex != null
                        ? "Snapshot serialization completed and is waiting for checkpoint publication."
                        : "Snapshot checkpoint flushed, but DotNext did not create a snapshot.",
                diagnostics));
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
        bool supportedTransport = raft.UsesSupportedTransport;
        bool configured = node.IsConsensusMember && raft.HasMinimumConfiguration && supportedTransport;
        bool ready = requested && configured && raftClusterRegistered;
        string status = raftFinality && node.IsEdge
            ? "edge_node_uses_remote_consensus"
            : !requested
            ? "not_requested"
            : !configured
                ? supportedTransport ? "missing_configuration" : "unsupported_transport"
                : !raftClusterRegistered
                    ? "cluster_not_registered"
                    : "ready";

        return new RaftStatusResponse(
            requested,
            configured,
            raftClusterRegistered,
            ready,
            status,
            string.IsNullOrWhiteSpace(raft.Transport) ? "Tcp" : raft.Transport,
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
        if (!requested || !raft.HasMinimumConfiguration || !raft.UsesSupportedTransport)
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
            !raft.HasMinimumConfiguration ||
            !raft.UsesSupportedTransport)
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

    private static bool IsValidAdminToken(IConfiguration configuration, string suppliedToken)
    {
        string configuredToken = configuration["NodeAdminToken"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configuredToken) || string.IsNullOrWhiteSpace(suppliedToken))
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(configuredToken);
        byte[] actual = Encoding.UTF8.GetBytes(suppliedToken);
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(expected, actual);
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
    string Transport,
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
    bool Operational,
    string OperationalStatus,
    long? LastCommittedIndex,
    long? LastAppliedIndex,
    string? Leader,
    IReadOnlyList<RaftMemberRuntimeResponse> Members,
    RaftSnapshotDiagnostics? Snapshot,
    RaftTransportProofResponse TransportProof);

public sealed record RaftTransportProofResponse(
    string ConfiguredTransport,
    string ActualTransport,
    string LocalIrohNodeId,
    string ConnectionPath,
    string IrohTransportMode,
    int DirectAddressCount,
    string Alpn);

public sealed record RaftPeerResponse(
    string Id,
    string EndPoint);

public sealed record RaftMemberRuntimeResponse(
    string Endpoint,
    bool IsLeader,
    bool IsRemote,
    string Status);

public sealed record RaftSnapshotTriggerRequest(string AdminToken);

public sealed record RaftSnapshotTriggerResponse(
    bool Success,
    string Message,
    RaftSnapshotDiagnostics? Snapshot);

public sealed record ContributionProofResponse(
    string ProjectId,
    long Epoch,
    string ProducerPublicKey,
    int ProducerScore,
    string ScoreSnapshotHash,
    IReadOnlyList<string> EvidenceBlockHashes);

#pragma warning restore DOTNEXT001
