using Blockchain.UI.Models;

namespace Blockchain.UI.Application.Clients;

public interface IProjectRepositoryDataService
{
    Task<ProjectRepositoryData> LoadAsync(string projectId);
}

public sealed record ProjectRepositoryData(
    IReadOnlyList<CommitPayloadUI> Commits,
    IReadOnlyList<ArtifactPayloadUI> Artifacts);
