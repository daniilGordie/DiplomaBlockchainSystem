using Blockchain.Core.Constants;

namespace Blockchain.Core;

public sealed class BlockMiner
{
    public void Mine(Block block)
    {
        do
        {
            block.Nonce++;
            block.Hash = block.CalculateHash();
        }
        while (!block.Hash.StartsWith(NetworkParameters.TargetPrefix));
    }
}
