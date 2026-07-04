using Blockchain.Application.Analytics;
using Blockchain.Core;
using Blockchain.Core.Constants;

namespace Blockchain.Infrastructure.Services;

public sealed class CoreBlockAuditVerifier : IBlockAuditVerifier
{
    public bool HasValidHash(BlockSnapshot block)
    {
        var coreBlock = ToCoreBlock(block);
        return coreBlock.Hash == coreBlock.CalculateHash();
    }

    public bool HasValidSignature(BlockSnapshot block)
    {
        return ToCoreBlock(block).VerifySignature();
    }

    public bool HasValidProofOfWork(BlockSnapshot block)
    {
        if (!NetworkParameters.RequireProofOfWork)
        {
            return true;
        }

        return block.Hash.StartsWith(NetworkParameters.TargetPrefix, StringComparison.Ordinal);
    }

    private static Block ToCoreBlock(BlockSnapshot block)
    {
        return new Block
        {
            Index = block.Index,
            Timestamp = block.Timestamp,
            Data = block.Data,
            PreviousHash = block.PreviousHash,
            Hash = block.Hash,
            ValidatorPublicKey = block.ValidatorPublicKey,
            Signature = block.Signature,
            Nonce = block.Nonce,
            ChannelId = block.ChannelId,
            TimestampUnixSeconds = block.TimestampUnixSeconds
        };
    }
}
