using Blockchain.Application.Git;
using Blockchain.Application.Security;

namespace Blockchain.Tests;

public class AuthorizeReadRequestUseCaseTests
{
    [Fact]
    public void Execute_ShouldAuthorizeValidReadRequest()
    {
        var useCase = CreateUseCase();

        var result = useCase.Execute(CreateCommand(nonce: "nonce-1"));

        Assert.True(result.Authorized);
    }

    [Fact]
    public void Execute_ShouldRejectMismatchedBoundPublicKey()
    {
        var useCase = CreateUseCase(boundPublicKey: "other-public-key");

        var result = useCase.Execute(CreateCommand(nonce: "nonce-2"));

        Assert.Equal(AuthorizeReadRequestStatus.PublicKeyMismatch, result.Status);
    }

    [Fact]
    public void Execute_ShouldRejectReplayedNonce()
    {
        var useCase = CreateUseCase();
        var command = CreateCommand(nonce: "nonce-3");

        var first = useCase.Execute(command);
        var replay = useCase.Execute(command);

        Assert.True(first.Authorized);
        Assert.Equal(AuthorizeReadRequestStatus.ReplayedNonce, replay.Status);
    }

    private static AuthorizeReadRequestCommand CreateCommand(string nonce)
    {
        return new AuthorizeReadRequestCommand(
            "PROJECT:ProjectA:TASKS",
            "Alice",
            "alice-public-key",
            "valid-signature",
            DateTime.UtcNow.ToString("O"),
            nonce);
    }

    private static AuthorizeReadRequestUseCase CreateUseCase(string boundPublicKey = "alice-public-key")
    {
        return new AuthorizeReadRequestUseCase(
            new FakeMembershipReader(boundPublicKey),
            new FakeReplayGuard(),
            new FakeSignatureVerifier(),
            new FakeClock());
    }

    private sealed class FakeMembershipReader : IProjectMembershipReader
    {
        private readonly string _boundPublicKey;

        public FakeMembershipReader(string boundPublicKey)
        {
            _boundPublicKey = boundPublicKey;
        }

        public string? GetUserPublicKey(string userName) => _boundPublicKey;

        public string GetUserRole(string projectId, string userName) => "Owner";
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
