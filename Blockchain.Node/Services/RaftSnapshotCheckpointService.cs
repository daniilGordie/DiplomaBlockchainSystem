using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

#pragma warning disable DOTNEXT001

public sealed class RaftSnapshotCheckpointService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ConsensusOptions _consensus;
    private readonly NexusNodeOptions _node;
    private readonly RaftOptions _raft;
    private readonly ILogger<RaftSnapshotCheckpointService> _logger;
    private long _lastCheckpointIndex;

    public RaftSnapshotCheckpointService(
        IServiceProvider services,
        IOptions<ConsensusOptions> consensus,
        IOptions<NexusNodeOptions> node,
        IOptions<RaftOptions> raft,
        ILogger<RaftSnapshotCheckpointService> logger)
    {
        _services = services;
        _consensus = consensus.Value;
        _node = node.Value;
        _raft = raft.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_raft.Snapshot.Enabled ||
            !_node.IsConsensusMember ||
            !string.Equals(_consensus.FinalityMode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                var stateMachine = _services.GetService<RaftBlockStateMachine>();
                var cluster = _services.GetService<IRaftCluster>();
                if (stateMachine == null || cluster?.AuditTrail is not WriteAheadLog wal)
                {
                    continue;
                }

                var diagnostics = stateMachine.GetDiagnostics();
                if (!diagnostics.StateMachineHealthy)
                {
                    _logger.LogCritical(
                        "[Raft] Snapshot checkpoint stopped because the state machine is faulted: {Failure}",
                        diagnostics.StateMachineFailure);
                    return;
                }

                if (diagnostics.CurrentSnapshotIndex is not { } snapshotIndex)
                {
                    continue;
                }

                long publishedSnapshotIndex = diagnostics.PublishedSnapshotIndex ?? 0L;
                if (publishedSnapshotIndex >= snapshotIndex)
                {
                    _lastCheckpointIndex = Math.Max(_lastCheckpointIndex, publishedSnapshotIndex);
                    continue;
                }

                if (cluster.LeadershipToken.IsCancellationRequested)
                {
                    continue;
                }

                bool checkpointCommitted = await cluster.ReplicateAsync(
                    RaftBlockStateMachine.SnapshotCheckpointPayload,
                    $"snapshot-checkpoint:{snapshotIndex}",
                    stoppingToken);
                if (!checkpointCommitted)
                {
                    _logger.LogWarning("[Raft] Snapshot checkpoint entry for index {SnapshotIndex} did not reach majority commit.", snapshotIndex);
                    continue;
                }

                using var publishTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                publishTimeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, _raft.RequestTimeoutMilliseconds * 2L)));
                while ((stateMachine.GetDiagnostics().PublishedSnapshotIndex ?? 0L) < snapshotIndex)
                {
                    await Task.Delay(50, publishTimeout.Token);
                }

                await cluster.ApplyReadBarrierAsync(stoppingToken);
                await wal.FlushAsync(stoppingToken);
                _lastCheckpointIndex = snapshotIndex;
                _logger.LogInformation(
                    "[Raft] Snapshot checkpoint published and flushed at index {SnapshotIndex}.",
                    snapshotIndex);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Raft] Snapshot checkpoint failed.");
            }
        }
    }
}

#pragma warning restore DOTNEXT001
