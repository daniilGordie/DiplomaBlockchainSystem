using Blockchain.Core.Constants;

namespace Blockchain.Core;

public sealed class BlockMiner
{
    public void Mine(Block block)
    {
        if (!NetworkParameters.RequireProofOfWork)
        {
            block.Hash = block.CalculateHash();
            return;
        }

        do
        {
            block.Nonce++;
            block.Hash = block.CalculateHash();
        }
        while (!block.Hash.StartsWith(NetworkParameters.TargetPrefix));
    }
}
