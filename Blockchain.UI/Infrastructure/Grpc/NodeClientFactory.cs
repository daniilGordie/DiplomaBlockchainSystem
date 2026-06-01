using Blockchain.Node;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;

namespace Blockchain.UI.Infrastructure.Grpc;

public sealed class NodeClientFactory : INodeClientFactory
{
    public BlockchainService.BlockchainServiceClient Create(string nodeUrl)
    {
        var httpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler());
        var channel = GrpcChannel.ForAddress(nodeUrl, new GrpcChannelOptions
        {
            HttpHandler = httpHandler
        });

        return new BlockchainService.BlockchainServiceClient(channel);
    }
}
