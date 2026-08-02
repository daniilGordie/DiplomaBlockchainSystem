using System.Security.Cryptography;
using System.Text;
using Blockchain.Core;
using Blockchain.Core.Contracts;

namespace Blockchain.Application.Blocks;

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

public sealed record ReceivePeerBlockCommand(Block Block);

public sealed record BlockWriteResult(bool Success, string Message, string ChannelId);
