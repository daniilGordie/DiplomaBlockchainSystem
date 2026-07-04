using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

public sealed class DotNextRaftClusterHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ConsensusOptions _consensusOptions;
    private readonly ILogger<DotNextRaftClusterHostedService> _logger;
    private RaftCluster? _raftCluster;

    public DotNextRaftClusterHostedService(
        IServiceProvider services,
        IOptions<ConsensusOptions> consensusOptions,
        ILogger<DotNextRaftClusterHostedService> logger)
    {
        _services = services;
        _consensusOptions = consensusOptions.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_consensusOptions.FinalityMode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("[Raft] DotNext cluster host is registered but finality mode is {FinalityMode}; startup skipped.", _consensusOptions.FinalityMode);
            return;
        }

        _logger.LogInformation("[Raft] Starting DotNext cluster host.");
        _raftCluster = _services.GetRequiredService<RaftCluster>();
        await _raftCluster.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_consensusOptions.FinalityMode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _logger.LogInformation("[Raft] Stopping DotNext cluster host.");
        if (_raftCluster != null)
        {
            await _raftCluster.StopAsync(cancellationToken);
        }
    }
}
