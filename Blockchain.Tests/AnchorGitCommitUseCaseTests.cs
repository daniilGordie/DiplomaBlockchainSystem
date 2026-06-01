using Blockchain.Application.Git;

namespace Blockchain.Tests;

public class AnchorGitCommitUseCaseTests
{
    [Fact]
    public void Execute_ShouldBuildCodeCommitPayload()
    {
        var bindings = new FakeBindingStore();
        var useCase = CreateUseCase(bindings);

        var result = useCase.Execute(new AnchorGitCommitCommand(
            "team/repo",
            "abcdef1234567890",
            "Initial commit",
            "Alice",
            "bafyPatch",
            "ProjectA",
            "GitHubPush",
            "main"));

        Assert.True(result.ShouldAnchor);
        Assert.NotNull(result.Payload);
        Assert.Equal("CodeCommit", result.Payload!.Type);
        Assert.Equal("GitEvent", result.Payload.Source);
        Assert.Equal("team/repo", result.Payload.Repository);
        Assert.Equal("ProjectA", bindings.Bindings["team/repo"]);
    }

    [Fact]
    public void Execute_ShouldRejectRepositoryBindingConflict()
    {
        var bindings = new FakeBindingStore();
        bindings.Bindings["team/repo"] = "OtherProject";
        var useCase = CreateUseCase(bindings);

        var result = useCase.Execute(new AnchorGitCommitCommand(
            "team/repo",
            "abcdef1234567890",
            "Initial commit",
            "Alice",
            "",
            "ProjectA",
            "NexusGitHook",
            "main"));

        Assert.Equal(AnchorGitCommitStatus.BindingConflict, result.Status);
    }

    [Fact]
    public void Execute_ShouldIgnoreDuplicateCommit()
    {
        var useCase = CreateUseCase(new FakeBindingStore());
        var command = new AnchorGitCommitCommand(
            "team/repo",
            "abcdef1234567890",
            "Initial commit",
            "Alice",
            "",
            "ProjectA",
            "NexusGitHook",
            "main");

        var first = useCase.Execute(command);
        var duplicate = useCase.Execute(command);

        Assert.True(first.ShouldAnchor);
        Assert.Equal(AnchorGitCommitStatus.DuplicateIgnored, duplicate.Status);
    }

    private static AnchorGitCommitUseCase CreateUseCase(FakeBindingStore bindings)
    {
        return new AnchorGitCommitUseCase(
            bindings,
            new FakeReplayGuard(),
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

    private sealed class FakeReplayGuard : IRequestReplayGuard
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public bool TryRegister(string key, DateTime nowUtc) => _keys.Add(key);
    }

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
