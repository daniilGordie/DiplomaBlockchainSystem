using Blockchain.Core.Contracts;

namespace Blockchain.Core;

public interface IChainReader
{
    bool HasBlocks(string channelId = "System");
    Block? GetLatestBlock(string channelId = "System");
    bool BlockExists(string blockHash, string channelId = "System");
    List<string> GetKnownChannels();
    List<Block> LoadChain(string channelId = "System");
    List<Block> LoadLatestBlocks(string channelId = "System", int count = 100);
}

public interface IChainWriter
{
    void SaveBlock(Block block, string channelId = "System");
    void ReplaceChain(string channelId, List<Block> blocks);
}

public interface IPendingBlockStore
{
    void SavePendingBlock(Block block, string reason);
    List<Block> LoadPendingChildren(string previousHash, string channelId);
    void RemovePendingBlock(string blockHash);
}

public interface IBlockStore :
    IChainReader,
    IChainWriter,
    IPendingBlockStore
{
}

public interface IUserProjectReader
{
    List<string> GetUserProjects(string userName);
}

public interface IProjectMembershipStore : IUserProjectReader
{
    string GetUserRole(string projectId, string userName);
}

public interface IBlockchainStore :
    IBlockStore,
    IProjectMembershipStore,
    IUserProjectReader,
    ISmartContractState,
    ISmartContractStateReader
{
}
