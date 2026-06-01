namespace Blockchain.Application.Git;

public sealed record AnchorGitCommitCommand(
    string Repository,
    string CommitHash,
    string Message,
    string Author,
    string PatchCid,
    string ProjectId,
    string Provider,
    string Branch);
