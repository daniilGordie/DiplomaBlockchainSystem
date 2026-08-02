namespace Blockchain.Node.Services;

public sealed class IrohInboundPump : BackgroundService
{
    private readonly IrohSidecarClient _sidecarClient;
    private readonly GrpcBlockProcessor _blockProcessor;
    private readonly ProjectResponseCache _projectResponseCache;
    private readonly P2POptions _options;
    private readonly ILogger<IrohInboundPump> _logger;

    public IrohInboundPump(
        IrohSidecarClient sidecarClient,
        GrpcBlockProcessor blockProcessor,
        ProjectResponseCache projectResponseCache,
        Microsoft.Extensions.Options.IOptions<P2POptions> options,
        ILogger<IrohInboundPump> logger)
    {
        _sidecarClient = sidecarClient;
        _blockProcessor = blockProcessor;
        _projectResponseCache = projectResponseCache;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Iroh.Enabled)
        {
            return;
        }

        int delayMs = Math.Clamp(_options.Iroh.PollIntervalMilliseconds, 250, 30000);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var block in await _sidecarClient.DrainEventsAsync(stoppingToken))
                {
                    var result = await _blockProcessor.ProcessReceivedAsync(block, stoppingToken);
                    if (result.Success)
                    {
                        _projectResponseCache.InvalidateProject(result.ChannelId);
                    }
                    else
                    {
                        _logger.LogWarning("[Iroh] Rejected inbound block {Hash}: {Message}", block.Hash, result.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Iroh] Inbound event polling failed: {Message}", ex.Message);
            }

            await Task.Delay(delayMs, stoppingToken);
        }
    }
}
