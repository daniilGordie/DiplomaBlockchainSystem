using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;

namespace Blockchain.Infrastructure.Services;

public sealed class DatabaseReplayStoreFactory : IReplayStoreFactory
{
    private readonly Dictionary<IBlockchainStore, string> _paths = new();

    public IBlockchainStore CreateReplayStore()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nexus_adopt_{Guid.NewGuid():N}.db");
        var store = new DatabaseManager(path, "");
        _paths[store] = path;
        return store;
    }

    public void CleanupReplayStore(IBlockchainStore replayStore)
    {
        if (_paths.Remove(replayStore, out var path))
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
