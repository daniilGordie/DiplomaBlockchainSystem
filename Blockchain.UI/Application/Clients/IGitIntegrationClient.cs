using Blockchain.UI.Models;

namespace Blockchain.UI.Application.Clients;

public interface IGitIntegrationClient
{
    Task<IntegrationStatusUI> GetStatusAsync(string nodeUrl);
    Task<GitRepositoryConnectResult> ConnectRepositoryAsync(GitRepositoryConnectCommand command);
}

public sealed record GitRepositoryConnectCommand(
    string NodeUrl,
    string ProjectId,
    string Repository,
    string User,
    string UserPublicKey,
    string UserSignature,
    string Timestamp);

public sealed record GitRepositoryConnectResult(
    bool Success,
    string Repository,
    string Message);
