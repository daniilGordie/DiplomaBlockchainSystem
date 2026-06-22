using System.Security.Cryptography;
using System.Text;
using Blockchain.Core;
using Blockchain.Core.Contracts;

namespace Blockchain.Application.Blocks;

public sealed class BroadcastLocalBlockUseCase
{
    private readonly BlockchainManager _blockchainManager;

    public BroadcastLocalBlockUseCase(BlockchainManager blockchainManager)
    {
        _blockchainManager = blockchainManager;
    }

    public BlockWriteResult Execute(Block block)
    {
        bool accepted = _blockchainManager.ProcessPeerBlock(block);
        return new BlockWriteResult(accepted, accepted ? "Accepted" : "Rejected", block.ChannelId);
    }
}

public sealed class ReceivePeerBlockUseCase
{
    private readonly BlockchainManager _blockchainManager;
    private readonly ISmartContractStateReader _smartContractState;

    public ReceivePeerBlockUseCase(
        BlockchainManager blockchainManager,
        ISmartContractStateReader smartContractState)
    {
        _blockchainManager = blockchainManager;
        _smartContractState = smartContractState;
    }

    public BlockWriteResult Execute(ReceivePeerBlockCommand command)
    {
        var contract = new TaskContract();
        if (!contract.Validate(command.Block.Data, command.Block.ValidatorPublicKey, _smartContractState))
        {
            return new BlockWriteResult(false, "Access Denied by Smart Contract", command.Block.ChannelId);
        }

        bool accepted = _blockchainManager.ProcessPeerBlock(command.Block);
        return new BlockWriteResult(
            accepted,
            accepted ? "Block anchored successfully via PoC" : "Rejected by blockchain validation",
            command.Block.ChannelId);
    }

}

public sealed class AdoptPeerChainUseCase
{
    private readonly BlockchainManager _blockchainManager;

    public AdoptPeerChainUseCase(BlockchainManager blockchainManager)
    {
        _blockchainManager = blockchainManager;
    }

    public bool Execute(string channelId, List<Block> candidateChain)
    {
        return _blockchainManager.TryAdoptChain(channelId, candidateChain);
    }
}

public sealed class MineAndAppendBlockUseCase
{
    private readonly BlockchainManager _blockchainManager;

    public MineAndAppendBlockUseCase(BlockchainManager blockchainManager)
    {
        _blockchainManager = blockchainManager;
    }

    public bool Execute(Block block)
    {
        return _blockchainManager.AddBlock(block);
    }
}

public sealed record ReceivePeerBlockCommand(Block Block);

public sealed record BlockWriteResult(bool Success, string Message, string ChannelId);
