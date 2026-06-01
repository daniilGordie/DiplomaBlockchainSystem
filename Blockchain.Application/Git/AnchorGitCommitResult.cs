namespace Blockchain.Application.Git;

public enum AnchorGitCommitStatus
{
    ReadyToAnchor,
    DuplicateIgnored,
    InvalidRequest,
    BindingConflict
}

public sealed record AnchorGitCommitPayload(
    string Type,
    string Source,
    string Provider,
    string Repository,
    string Branch,
    string CommitHash,
    string Message,
    string User,
    string PatchCid,
    string ProjectId,
    string Timestamp);

public sealed record AnchorGitCommitResult(
    AnchorGitCommitStatus Status,
    string Message,
    string ProjectId = "",
    AnchorGitCommitPayload? Payload = null)
{
    public bool ShouldAnchor => Status == AnchorGitCommitStatus.ReadyToAnchor && Payload != null;
}
