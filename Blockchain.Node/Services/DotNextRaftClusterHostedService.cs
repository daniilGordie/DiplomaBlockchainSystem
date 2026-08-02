using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

#pragma warning disable DOTNEXT001

public sealed class DotNextRaftClusterHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly NexusNodeOptions _nodeOptions;
    private readonly ILogger<DotNextRaftClusterHostedService> _logger;
    private RaftCluster? _raftCluster;

    public DotNextRaftClusterHostedService(
        IServiceProvider services,
        IOptions<NexusNodeOptions> nodeOptions,
        ILogger<DotNextRaftClusterHostedService> logger)
    {
        _services = services;
        _nodeOptions = nodeOptions.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_nodeOptions.IsConsensusMember)
        {
            _logger.LogInformation("[Raft] Node role is {NodeRole}; DotNext cluster startup skipped.", _nodeOptions.EffectiveRole);
            return;
        }

        _logger.LogInformation("[Raft] Starting DotNext cluster host.");
        _raftCluster = _services.GetRequiredService<RaftCluster>();
        await _raftCluster.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_nodeOptions.IsConsensusMember)
        {
            return;
        }

        _logger.LogInformation("[Raft] Stopping DotNext cluster host.");
        if (_raftCluster != null)
        {
            try
            {
                await _raftCluster.StopAsync(cancellationToken);
            }
            finally
            {
                if (_raftCluster.AuditTrail is WriteAheadLog wal)
                {
                    await wal.DisposeAsync();
                }
            }
        }
    }
}

#pragma warning restore DOTNEXT001
