namespace Blockchain.Application.Git;

public sealed record ConnectGitRepositoryCommand(
    string ProjectId,
    string Repository,
    string User,
    string Timestamp,
    string UserPublicKey,
    string UserSignature);
