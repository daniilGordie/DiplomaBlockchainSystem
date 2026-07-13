using Blockchain.Node.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class IrohMessageSecurityTests
{
    [Fact]
    public void Validate_ShouldAcceptSignedMessage()
    {
        using var fixture = new IdentityFixture();
        string payloadHash = IrohMessageSecurity.HashPayload("block-hash");
        var auth = fixture.Security.CreateAuth(IrohMessageOperations.SubmitBlock, payloadHash);

        var result = fixture.Security.Validate(auth, IrohMessageOperations.SubmitBlock, payloadHash);

        Assert.True(result.Accepted);
        Assert.Equal("node-a", result.NodeId);
    }

    [Fact]
    public void Validate_ShouldRejectReplayNonce()
    {
        using var fixture = new IdentityFixture();
        string payloadHash = IrohMessageSecurity.HashPayload("block-hash");
        var auth = fixture.Security.CreateAuth(IrohMessageOperations.SubmitBlock, payloadHash);

        Assert.True(fixture.Security.Validate(auth, IrohMessageOperations.SubmitBlock, payloadHash).Accepted);
        var replay = fixture.Security.Validate(auth, IrohMessageOperations.SubmitBlock, payloadHash);

        Assert.False(replay.Accepted);
        Assert.Contains("nonce", replay.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ShouldRejectPayloadMismatch()
    {
        using var fixture = new IdentityFixture();
        var auth = fixture.Security.CreateAuth(
            IrohMessageOperations.CommittedSince,
            IrohMessageSecurity.HashPayload("System|1|abc"));

        var result = fixture.Security.Validate(
            auth,
            IrohMessageOperations.CommittedSince,
            IrohMessageSecurity.HashPayload("System|2|abc"));

        Assert.False(result.Accepted);
        Assert.Contains("signature", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class IdentityFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nexus-identity-{Guid.NewGuid():N}");

        public IdentityFixture()
        {
            Directory.CreateDirectory(_directory);
            var options = new P2POptions
            {
                NodeId = "node-a",
                IdentityKeyPath = Path.Combine(_directory, "node.key")
            };
            var identity = new NodeIdentity(
                Options.Create(options),
                NullLogger<NodeIdentity>.Instance);
            Security = new IrohMessageSecurity(
                identity,
                new WebhookReplayGuard(TimeSpan.FromMinutes(5)),
                Options.Create(options),
                NullLogger<IrohMessageSecurity>.Instance);
        }

        public IrohMessageSecurity Security { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
