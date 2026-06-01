namespace Blockchain.Core;

public sealed class PendingBlockConnector
{
    private readonly IPendingBlockStore _pendingBlockStore;

    public PendingBlockConnector(IPendingBlockStore pendingBlockStore)
    {
        _pendingBlockStore = pendingBlockStore;
    }

    public void ConnectChildren(string channelId, string parentHash, Func<Block, bool> processBlock)
    {
        foreach (var pending in _pendingBlockStore.LoadPendingChildren(parentHash, channelId))
        {
            if (processBlock(pending))
            {
                _pendingBlockStore.RemovePendingBlock(pending.Hash);
            }
        }
    }
}
