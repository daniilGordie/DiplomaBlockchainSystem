using Blockchain.Node.Services;
using Microsoft.AspNetCore.Connections;

public sealed class IrohRaftTransportTests
{
    [Fact]
    public void CreateEndPoint_ShouldParseIrohUri()
    {
        var endpoint = IrohRaftTransport.CreateEndPoint("iroh://nodeabc123");

        var uriEndpoint = Assert.IsType<UriEndPoint>(endpoint);
        Assert.Equal("iroh", uriEndpoint.Uri.Scheme);
        Assert.Equal("nodeabc123", IrohRaftTransport.GetNodeId(endpoint));
    }

    [Fact]
    public void CreateEndPoint_ShouldRejectTcpEndpoint()
    {
        Assert.Throws<InvalidOperationException>(() => IrohRaftTransport.CreateEndPoint("localhost:6041"));
    }

    [Fact]
    public void ParseLoopbackEndPoint_ShouldRejectPublicAddress()
    {
        Assert.Throws<InvalidOperationException>(() => IrohRaftTransport.ParseLoopbackEndPoint("0.0.0.0:60411", 60411));
    }
}
