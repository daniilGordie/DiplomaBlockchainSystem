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
        string signableData = $"{command.Block.Index}{command.TimestampText}{command.Block.Data}{command.Block.PreviousHash}";
        if (!VerifySignature(signableData, command.Block.Signature, command.Block.ValidatorPublicKey))
        {
            return new BlockWriteResult(false, "Crypto Fraud Detected: Invalid Signature", command.Block.ChannelId);
        }

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

    private static bool VerifySignature(string data, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            byte[] signatureBytes = Convert.FromBase64String(signatureBase64);
            if (signatureBytes.Length != 64)
            {
                return false;
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

            return ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(data),
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            return false;
        }
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

public sealed record ReceivePeerBlockCommand(Block Block, string TimestampText);

public sealed record BlockWriteResult(bool Success, string Message, string ChannelId);
