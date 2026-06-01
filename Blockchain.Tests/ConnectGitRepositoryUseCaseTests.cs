using Blockchain.Application.Git;

namespace Blockchain.Tests;

public class ConnectGitRepositoryUseCaseTests
{
    [Fact]
    public void Execute_ShouldConnectRepositoryForProjectManager()
    {
        var bindings = new FakeBindingStore();
        var useCase = CreateUseCase(bindings, role: "Manager");

        var result = useCase.Execute(new ConnectGitRepositoryCommand(
            "ProjectA",
            "https://github.com/owner/repo.git",
            "Alice",
            DateTime.UtcNow.ToString("O"),
            "alice-public-key",
            "valid-signature"));

        Assert.True(result.Success);
        Assert.Equal("github.com/owner/repo", result.Repository);
        Assert.Equal("ProjectA", bindings.Bindings["github.com/owner/repo"]);
    }

    [Fact]
    public void Execute_ShouldRejectDeveloperRole()
    {
        var useCase = CreateUseCase(new FakeBindingStore(), role: "Developer");

        var result = useCase.Execute(new ConnectGitRepositoryCommand(
            "ProjectA",
            "owner/repo",
            "Alice",
            DateTime.UtcNow.ToString("O"),
            "alice-public-key",
            "valid-signature"));

        Assert.Equal(ConnectGitRepositoryStatus.Forbidden, result.Status);
    }

    [Fact]
    public void Execute_ShouldRejectReplayedRequest()
    {
        var useCase = CreateUseCase(new FakeBindingStore(), role: "Owner");
        string timestamp = DateTime.UtcNow.ToString("O");
        var command = new ConnectGitRepositoryCommand(
            "ProjectA",
            "owner/repo",
            "Alice",
            timestamp,
            "alice-public-key",
            "valid-signature");

        var first = useCase.Execute(command);
        var replay = useCase.Execute(command);

        Assert.True(first.Success);
        Assert.Equal(ConnectGitRepositoryStatus.DuplicateRequest, replay.Status);
    }

    private static ConnectGitRepositoryUseCase CreateUseCase(FakeBindingStore bindings, string role)
    {
        return new ConnectGitRepositoryUseCase(
            new FakeMembershipReader(role),
            bindings,
            new FakeReplayGuard(),
            new FakeSignatureVerifier(),
            new FakeClock());
    }

    private sealed class FakeBindingStore : IGitRepositoryBindingStore
    {
        public Dictionary<string, string> Bindings { get; } = new(StringComparer.Ordinal);

        public bool TryValidateOrBind(string repository, string projectId, out string message)
        {
            message = "";
            if (Bindings.TryGetValue(repository, out var boundProject) && boundProject != projectId)
            {
                message = "binding conflict";
                return false;
            }

            Bindings[repository] = projectId;
            return true;
        }
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
