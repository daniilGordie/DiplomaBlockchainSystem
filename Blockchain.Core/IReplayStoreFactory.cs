namespace Blockchain.Core;

public interface IReplayStoreFactory
{
    IBlockchainStore CreateReplayStore();
    void CleanupReplayStore(IBlockchainStore replayStore);
}
