using Blockchain.Node.Services;
using Microsoft.AspNetCore.Connections;
using System.Net;
using System.Net.Sockets;

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

    [Fact]
    public async Task NetworkStreamConnectionContext_ShouldExposeAndCancelConnectionClosedToken()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endPoint = Assert.IsType<IPEndPoint>(listener.LocalEndpoint);
        using var client = new TcpClient();
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(endPoint.Address, endPoint.Port);
        using var accepted = await acceptTask;
        var context = new NetworkStreamConnectionContext(
            client,
            client.GetStream(),
            client.Client.LocalEndPoint,
            client.Client.RemoteEndPoint);

        Assert.True(context.ConnectionClosed.CanBeCanceled);
        Assert.False(context.ConnectionClosed.IsCancellationRequested);

        context.Abort(new ConnectionAbortedException("test shutdown"));

        Assert.True(context.ConnectionClosed.IsCancellationRequested);
        await context.DisposeAsync();
    }
}
