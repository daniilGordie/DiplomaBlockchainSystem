using Blockchain.Node;

namespace Blockchain.UI.Infrastructure.Grpc;

public interface INodeClientFactory
{
    BlockchainService.BlockchainServiceClient Create(string nodeUrl);
}
