using System.Text.RegularExpressions;

namespace Blockchain.Application.Git;

public sealed class AnchorGitCommitUseCase
{
    private static readonly Regex CommitHashPattern = new("^[a-fA-F0-9]+$", RegexOptions.Compiled);
    private static readonly Regex ProjectIdPattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex RepositoryPattern = new("^[A-Za-z0-9_.\\-/:]+$", RegexOptions.Compiled);

    private readonly IGitRepositoryBindingStore _bindingStore;
    private readonly IRequestReplayGuard _replayGuard;
    private readonly IClock _clock;

    public AnchorGitCommitUseCase(
        IGitRepositoryBindingStore bindingStore,
        IRequestReplayGuard replayGuard,
        IClock clock)
    {
        _bindingStore = bindingStore;
        _replayGuard = replayGuard;
        _clock = clock;
    }

    public AnchorGitCommitResult Execute(AnchorGitCommitCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Author) ||
            string.IsNullOrWhiteSpace(command.CommitHash))
        {
            return Invalid("Invalid git payload. Expected Nexus hook payload or supported Git provider push payload.");
        }

        if (!IsValidCommitHash(command.CommitHash))
        {
            return Invalid("Invalid commit hash format");
        }

        if (!IsValidProjectId(command.ProjectId))
        {
            return Invalid("Invalid project id");
        }

        if (!IsValidRepository(command.Repository))
        {
            return Invalid("Invalid repository field");
        }

        if (!_bindingStore.TryValidateOrBind(command.Repository, command.ProjectId, out var bindingMessage))
        {
            return new AnchorGitCommitResult(AnchorGitCommitStatus.BindingConflict, bindingMessage);
        }

        string replayKey = $"{command.ProjectId}:{command.Repository}:{command.CommitHash}".ToLowerInvariant();
        if (!_replayGuard.TryRegister(replayKey, _clock.UtcNow))
        {
            return new AnchorGitCommitResult(AnchorGitCommitStatus.DuplicateIgnored, "duplicate_ignored", command.ProjectId);
        }

        var payload = new AnchorGitCommitPayload(
            Type: "CodeCommit",
            Source: "GitEvent",
            Provider: string.IsNullOrWhiteSpace(command.Provider) ? "NexusGitHook" : command.Provider,
            Repository: command.Repository,
            Branch: command.Branch,
            CommitHash: command.CommitHash,
            Message: command.Message ?? string.Empty,
            User: command.Author,
            PatchCid: command.PatchCid ?? string.Empty,
            ProjectId: command.ProjectId,
            Timestamp: _clock.UtcNow.ToString("O"));

        return new AnchorGitCommitResult(AnchorGitCommitStatus.ReadyToAnchor, "ready", command.ProjectId, payload);
    }

    private static bool IsValidCommitHash(string commitHash)
    {
        return !string.IsNullOrWhiteSpace(commitHash)
            && commitHash.Length >= 7
            && commitHash.Length <= 64
            && CommitHashPattern.IsMatch(commitHash);
    }

    private static bool IsValidProjectId(string projectId)
    {
        return !string.IsNullOrWhiteSpace(projectId)
            && ProjectIdPattern.IsMatch(projectId);
    }

    private static bool IsValidRepository(string repository)
    {
        return !string.IsNullOrWhiteSpace(repository)
            && repository.Length <= 200
            && RepositoryPattern.IsMatch(repository);
    }

    private static AnchorGitCommitResult Invalid(string message)
    {
        return new AnchorGitCommitResult(AnchorGitCommitStatus.InvalidRequest, message);
    }
}
