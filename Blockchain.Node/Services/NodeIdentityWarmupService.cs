namespace Blockchain.Node.Services;

public sealed class NodeIdentityWarmupService : IHostedService
{
    private readonly NodeIdentity _nodeIdentity;
    private readonly ILogger<NodeIdentityWarmupService> _logger;

    public NodeIdentityWarmupService(NodeIdentity nodeIdentity, ILogger<NodeIdentityWarmupService> logger)
    {
        _nodeIdentity = nodeIdentity;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        string fingerprint = ComputeFingerprint(_nodeIdentity.PublicKey);
        _logger.LogInformation("[P2P] Node identity ready. Public key fingerprint: {Fingerprint}", fingerprint);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string ComputeFingerprint(string publicKey)
    {
        byte[] raw = Convert.FromBase64String(publicKey);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(raw);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
