using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Blockchain.Node.Services;

public static class IrohRaftTransport
{
    public const string Scheme = "iroh";

    public static EndPoint CreateEndPoint(string irohNodeId)
    {
        if (string.IsNullOrWhiteSpace(irohNodeId))
        {
            throw new InvalidOperationException("RAFT_IROH_NODE_ID is required when Raft:Transport=Iroh.");
        }

        string value = irohNodeId.Trim();
        if (!value.StartsWith("iroh://", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Contains(':', StringComparison.Ordinal) ||
                value.Contains('/', StringComparison.Ordinal) ||
                value.Contains('\\', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Invalid Iroh Raft endpoint '{irohNodeId}'. Use iroh://<node-id>.");
            }

            value = "iroh://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException($"Invalid Iroh Raft endpoint '{irohNodeId}'. Use iroh://<node-id>.");
        }

        return new UriEndPoint(uri);
    }

    public static string GetNodeId(EndPoint endpoint)
    {
        if (endpoint is UriEndPoint uriEndPoint &&
            string.Equals(uriEndPoint.Uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return uriEndPoint.Uri.Host;
        }

        throw new InvalidOperationException($"Raft-over-Iroh requires iroh:// endpoints. Actual endpoint: {endpoint}.");
    }

    public static IPEndPoint ParseLoopbackEndPoint(string value, int defaultPort)
    {
        string text = string.IsNullOrWhiteSpace(value) ? $"127.0.0.1:{defaultPort}" : value.Trim();
        var parts = text.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port) || port <= 0)
        {
            throw new InvalidOperationException($"Invalid loopback endpoint '{value}'. Use 127.0.0.1:<port>.");
        }

        if (!IPAddress.TryParse(parts[0], out var address))
        {
            throw new InvalidOperationException($"Invalid loopback address '{parts[0]}'.");
        }

        if (!IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("Iroh Raft local IPC endpoints must be loopback addresses.");
        }

        return new IPEndPoint(address, port);
    }
}

public sealed class IrohRaftEndPointComparer : IEqualityComparer<EndPoint>
{
    public bool Equals(EndPoint? x, EndPoint? y)
    {
        if (x == null || y == null)
        {
            return x == y;
        }

        return string.Equals(IrohRaftTransport.GetNodeId(x), IrohRaftTransport.GetNodeId(y), StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(EndPoint obj) =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(IrohRaftTransport.GetNodeId(obj));
}

public sealed class IrohRaftConnectionFactory : IConnectionFactory
{
    private readonly RaftOptions _raft;
    private readonly P2POptions _p2p;
    private readonly ILogger<IrohRaftConnectionFactory> _logger;

    public IrohRaftConnectionFactory(
        IOptions<RaftOptions> raftOptions,
        IOptions<P2POptions> p2pOptions,
        ILogger<IrohRaftConnectionFactory> logger)
    {
        _raft = raftOptions.Value;
        _p2p = p2pOptions.Value;
        _logger = logger;
    }

    public async ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        string remoteIrohNodeId = IrohRaftTransport.GetNodeId(endpoint);
        var controlEndPoint = IrohRaftTransport.ParseLoopbackEndPoint(_raft.IrohControlEndPoint, 60411);
        var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(controlEndPoint.Address, controlEndPoint.Port, cancellationToken);

        string token = _p2p.Iroh.LocalApiToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            client.Dispose();
            throw new InvalidOperationException("P2P:Iroh:LocalApiToken is required for Raft-over-Iroh.");
        }

        var stream = client.GetStream();
        string openLine = $"OPEN {Convert.ToBase64String(Encoding.UTF8.GetBytes(token))} {remoteIrohNodeId}\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(openLine), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        _logger.LogDebug("[Raft/Iroh] Opened outgoing stream to {RemoteIrohNodeId}.", remoteIrohNodeId);
        return new NetworkStreamConnectionContext(client, stream, localEndPoint: controlEndPoint, remoteEndPoint: endpoint);
    }
}

public sealed class IrohRaftConnectionListenerFactory : IConnectionListenerFactory
{
    private readonly RaftOptions _raft;
    private readonly ILogger<IrohRaftConnectionListenerFactory> _logger;

    public IrohRaftConnectionListenerFactory(
        IOptions<RaftOptions> raftOptions,
        ILogger<IrohRaftConnectionListenerFactory> logger)
    {
        _raft = raftOptions.Value;
        _logger = logger;
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var listenEndPoint = IrohRaftTransport.ParseLoopbackEndPoint(_raft.IrohNodeListenEndPoint, 60410);
        var listener = new IrohRaftConnectionListener(listenEndPoint, endpoint, _logger);
        listener.Start();
        return ValueTask.FromResult<IConnectionListener>(listener);
    }
}

public sealed class IrohRaftConnectionListener : IConnectionListener
{
    private readonly TcpListener _listener;
    private readonly EndPoint _publicEndPoint;
    private readonly ILogger _logger;

    public IrohRaftConnectionListener(IPEndPoint localEndPoint, EndPoint publicEndPoint, ILogger logger)
    {
        _listener = new TcpListener(localEndPoint);
        _publicEndPoint = publicEndPoint;
        _logger = logger;
    }

    public EndPoint EndPoint => _publicEndPoint;

    public void Start()
    {
        _listener.Start();
        _logger.LogInformation("[Raft/Iroh] Listening for local sidecar Raft streams at {LocalEndPoint}.", _listener.LocalEndpoint);
    }

    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var client = await _listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        return new NetworkStreamConnectionContext(client, stream, client.Client.LocalEndPoint, client.Client.RemoteEndPoint);
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}

public sealed class NetworkStreamConnectionContext : ConnectionContext
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly CancellationTokenSource _closed = new();

    public NetworkStreamConnectionContext(TcpClient client, Stream stream, EndPoint? localEndPoint, EndPoint? remoteEndPoint)
    {
        _client = client;
        _stream = stream;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        ConnectionId = Guid.NewGuid().ToString("N");
        Features = new FeatureCollection();
        Items = new ConnectionItems();
        Transport = new StreamDuplexPipe(stream);
    }

    public override string ConnectionId { get; set; }
    public override IFeatureCollection Features { get; }
    public override IDictionary<object, object?> Items { get; set; }
    public override IDuplexPipe Transport { get; set; }
    public override EndPoint? LocalEndPoint { get; set; }
    public override EndPoint? RemoteEndPoint { get; set; }
    public override CancellationToken ConnectionClosed { get; set; }

    public override void Abort(ConnectionAbortedException abortReason)
    {
        _closed.Cancel();
        _client.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        _closed.Cancel();
        await _stream.DisposeAsync();
        _client.Dispose();
        await base.DisposeAsync();
    }
}

public sealed class StreamDuplexPipe : IDuplexPipe
{
    public StreamDuplexPipe(Stream stream)
    {
        Input = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public PipeReader Input { get; }
    public PipeWriter Output { get; }
}
