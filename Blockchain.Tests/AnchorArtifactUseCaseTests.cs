using Blockchain.Application.Artifacts;
using Blockchain.Application.Git;

namespace Blockchain.Tests;

public class AnchorArtifactUseCaseTests
{
    private const string ValidCid = "bafybeigdyrzt5sfp7udm7hu76d6w3dgk3w5xk7gq5ztm5xj4n4cz";

    [Fact]
    public void Execute_ShouldBuildArtifactPayloadForProjectMember()
    {
        var useCase = CreateUseCase(role: "Developer");

        var result = useCase.Execute(CreateCommand());

        Assert.True(result.ShouldAnchor);
        Assert.NotNull(result.Payload);
        Assert.Equal("ArtifactRegistry", result.Payload!.Source);
        Assert.Equal("Register", result.Payload.Type);
        Assert.Equal(ValidCid, result.Payload.FileHash);
        Assert.Equal("Alice", result.Payload.RegisteredBy);
    }

    [Fact]
    public void Execute_ShouldRejectUserWithoutProjectRole()
    {
        var useCase = CreateUseCase(role: "None");

        var result = useCase.Execute(CreateCommand());

        Assert.Equal(AnchorArtifactStatus.Forbidden, result.Status);
    }

    [Fact]
    public void Execute_ShouldIgnoreDuplicateArtifactRequest()
    {
        var useCase = CreateUseCase(role: "Developer");
        var command = CreateCommand();

        var first = useCase.Execute(command);
        var duplicate = useCase.Execute(command);

        Assert.True(first.ShouldAnchor);
        Assert.Equal(AnchorArtifactStatus.DuplicateIgnored, duplicate.Status);
    }

    private static AnchorArtifactCommand CreateCommand()
    {
        return new AnchorArtifactCommand(
            "ProjectA",
            ValidCid,
            "report.pdf",
            1024,
            "application/pdf",
            "Alice",
            "Alice",
            "IPFS CID",
            DateTime.UtcNow.ToString("O"),
            "alice-public-key",
            "valid-signature");
    }

    private static AnchorArtifactUseCase CreateUseCase(string role)
    {
        return new AnchorArtifactUseCase(
            new FakeMembershipReader(role),
            new FakeReplayGuard(),
            new FakeSignatureVerifier(),
            new FakeClock());
    }

    private sealed class FakeMembershipReader : IProjectMembershipReader
    {
        private readonly string _role;

        public FakeMembershipReader(string role)
        {
            _role = role;
        }

        public string? GetUserPublicKey(string userName) => "alice-public-key";

        public string GetUserRole(string projectId, string userName) => _role;
    }

    private sealed class FakeReplayGuard : IRequestReplayGuard
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public bool TryRegister(string key, DateTime nowUtc) => _keys.Add(key);
    }

    private sealed class FakeSignatureVerifier : ISignatureVerifier
    {
        public bool Verify(string data, string signatureBase64, string publicKeyBase64)
        {
            return signatureBase64 == "valid-signature";
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
