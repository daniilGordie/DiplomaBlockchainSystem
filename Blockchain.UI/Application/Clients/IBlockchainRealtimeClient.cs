using Blockchain.Node;

namespace Blockchain.UI.Application.Clients;

public interface IBlockchainRealtimeClient : IAsyncDisposable
{
    Task ConnectAsync(string nodeUrl, Func<BlockModel, Task> onBlockReceived);
    Task<bool> JoinProjectsAsync(string? currentProjectId);
}
