namespace Blockchain.Application.Git;

public enum ConnectGitRepositoryStatus
{
    Connected,
    InvalidRequest,
    Unauthorized,
    Forbidden,
    DuplicateRequest,
    BindingConflict
}

public sealed record ConnectGitRepositoryResult(
    ConnectGitRepositoryStatus Status,
    string Message,
    string Repository = "",
    string ProjectId = "")
{
    public bool Success => Status == ConnectGitRepositoryStatus.Connected;
}
