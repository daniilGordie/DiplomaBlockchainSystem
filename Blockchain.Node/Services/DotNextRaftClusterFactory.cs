using DotNext.Buffers;
using DotNext.IO;
using DotNext.Net;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Net;
using System.Text.Json;

namespace Blockchain.Node.Services;

#pragma warning disable DOTNEXT001

public sealed class DotNextRaftClusterFactory
{
    private readonly RaftOptions _options;
    private readonly P2POptions _p2pOptions;
    private readonly RaftBlockStateMachine _stateMachine;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DotNextRaftClusterFactory> _logger;

    public DotNextRaftClusterFactory(
        IOptions<RaftOptions> options,
        IOptions<P2POptions> p2pOptions,
        RaftBlockStateMachine stateMachine,
        IServiceProvider services,
        ILoggerFactory loggerFactory,
        ILogger<DotNextRaftClusterFactory> logger)
    {
        _options = options.Value;
        _p2pOptions = p2pOptions.Value;
        _stateMachine = stateMachine;
        _services = services;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public RaftCluster CreateCluster()
    {
        if (!_options.UsesSupportedTransport)
        {
            throw new NotSupportedException(
                $"Unsupported Raft transport '{_options.Transport}'. Use Raft:Transport=Tcp or Raft:Transport=Iroh.");
        }

        if (!_options.HasMinimumConfiguration)
        {
            throw new InvalidOperationException(_options.UsesIrohTransport
                ? "Raft-over-Iroh requires Raft:NodeId and Raft:IrohNodeId."
                : "Raft cluster requires Raft:NodeId and Raft:PublicEndPoint.");
        }

        _logger.LogInformation("[Raft] Restoring the latest state machine snapshot before opening the Raft WAL.");
        _stateMachine.RestoreAsync().AsTask().GetAwaiter().GetResult();

        return _options.UsesIrohTransport ? CreateIrohCluster() : CreateTcpCluster();
    }

    private RaftCluster CreateTcpCluster()
    {
        if (!_options.HasMinimumConfiguration)
        {
            throw new InvalidOperationException("Raft cluster requires Raft:NodeId and Raft:PublicEndPoint.");
        }

        var publicEndPoint = ParsePublicEndPoint(_options.PublicEndPoint);
        var bindEndPoint = ToBindEndPoint(publicEndPoint);
        var configuration = new RaftCluster.TcpConfiguration(bindEndPoint)
        {
            PublicEndPoint = publicEndPoint,
            LowerElectionTimeout = Math.Max(150, _options.ElectionTimeoutMilliseconds),
            UpperElectionTimeout = Math.Max(300, _options.ElectionTimeoutMilliseconds * 2),
            RequestTimeout = TimeSpan.FromMilliseconds(Math.Max(500, _options.RequestTimeoutMilliseconds)),
            ColdStart = false,
            LoggerFactory = _loggerFactory
        };
        configuration.Metadata["NodeId"] = _options.NodeId;

        var configuredMembers = BuildConfiguredMembers(publicEndPoint);
        if (_options.UsePersistentMembership)
        {
            ConfigurePersistentMembership(
                configuration,
                configuredMembers,
                new EndPointEqualityComparer());
        }
        else
        {
            ConfigureInMemoryMembership(configuration, configuredMembers);
        }

        var logPath = Path.GetFullPath(_options.LogPath);
        Directory.CreateDirectory(logPath);
        Directory.CreateDirectory(Path.GetFullPath(_options.SnapshotPath));
        var auditTrail = new WriteAheadLog(
            new WriteAheadLog.Options
            {
                Location = logPath
            },
            _stateMachine);

        var cluster = new RaftCluster(configuration)
        {
            AuditTrail = auditTrail
        };

        _logger.LogInformation(
            "[Raft] Created DotNext TCP cluster node {NodeId} at {PublicEndPoint}; log path: {LogPath}; membership: {MembershipMode}.",
            _options.NodeId,
            publicEndPoint,
            logPath,
            _options.UsePersistentMembership ? Path.GetFullPath(_options.MembershipPath) : "in-memory");

        if (!_options.HasRemotePeers)
        {
            _logger.LogWarning("[Raft] Starting as a single-member cluster. Blocks can be finalized, but this network has no consensus fault tolerance until another consensus member is added.");
        }

        return cluster;
    }

    private RaftCluster CreateIrohCluster()
    {
        if (!_p2pOptions.Iroh.Enabled)
        {
            throw new InvalidOperationException("Raft:Transport=Iroh requires P2P:Iroh:Enabled=true.");
        }

        if (string.IsNullOrWhiteSpace(_p2pOptions.Iroh.LocalApiToken))
        {
            throw new InvalidOperationException("Raft:Transport=Iroh requires P2P:Iroh:LocalApiToken.");
        }

        var publicEndPoint = IrohRaftTransport.CreateEndPoint(_options.IrohNodeId);
        var connectionFactory = _services.GetRequiredService<IrohRaftConnectionFactory>();
        var listenerFactory = _services.GetRequiredService<IrohRaftConnectionListenerFactory>();
        var configuration = new RaftCluster.CustomTransportConfiguration(
            publicEndPoint,
            listenerFactory,
            connectionFactory)
        {
            PublicEndPoint = publicEndPoint,
            LowerElectionTimeout = Math.Max(150, _options.ElectionTimeoutMilliseconds),
            UpperElectionTimeout = Math.Max(300, _options.ElectionTimeoutMilliseconds * 2),
            RequestTimeout = TimeSpan.FromMilliseconds(Math.Max(500, _options.RequestTimeoutMilliseconds)),
            ColdStart = false,
            LoggerFactory = _loggerFactory,
            EndPointComparer = new IrohRaftEndPointComparer(),
            ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(500, _options.RequestTimeoutMilliseconds))
        };
        configuration.Metadata["NodeId"] = _options.NodeId;

        var configuredMembers = BuildConfiguredIrohMembers(publicEndPoint);
        if (_options.UsePersistentMembership)
        {
            ConfigurePersistentMembership(
                configuration,
                configuredMembers,
                new IrohRaftEndPointComparer());
        }
        else
        {
            ConfigureInMemoryMembership(configuration, configuredMembers);
        }

        var logPath = Path.GetFullPath(_options.LogPath);
        Directory.CreateDirectory(logPath);
        Directory.CreateDirectory(Path.GetFullPath(_options.SnapshotPath));
        var auditTrail = new WriteAheadLog(
            new WriteAheadLog.Options
            {
                Location = logPath
            },
            _stateMachine);

        var cluster = new RaftCluster(configuration)
        {
            AuditTrail = auditTrail
        };

        _logger.LogInformation(
            "[Raft] Created DotNext Iroh cluster node {NodeId} at iroh://{IrohNodeId}; log path: {LogPath}; membership: {MembershipMode}.",
            _options.NodeId,
            _options.IrohNodeId,
            logPath,
            _options.UsePersistentMembership ? Path.GetFullPath(_options.MembershipPath) : "in-memory");

        if (!_options.HasRemotePeers)
        {
            _logger.LogWarning("[Raft] Starting as a single-member Iroh cluster. Blocks can be finalized, but this network has no consensus fault tolerance until another consensus member is added.");
        }

        return cluster;
    }

    private List<EndPoint> BuildConfiguredMembers(EndPoint publicEndPoint)
    {
        var members = new List<EndPoint> { publicEndPoint };
        foreach (var peer in _options.Peers)
        {
            if (string.IsNullOrWhiteSpace(peer.EndPoint))
            {
                continue;
            }

            var peerEndPoint = ParsePublicEndPoint(peer.EndPoint);
            if (members.Any(member => AreEquivalentEndpoints(member, peerEndPoint)))
            {
                continue;
            }

            members.Add(peerEndPoint);
            _logger.LogInformation("[Raft] Configured peer {PeerId} at {PeerEndPoint}.", peer.Id, peerEndPoint);
        }

        return members;
    }

    private List<EndPoint> BuildConfiguredIrohMembers(EndPoint publicEndPoint)
    {
        var members = new List<EndPoint> { publicEndPoint };
        foreach (var peer in _options.Peers)
        {
            if (string.IsNullOrWhiteSpace(peer.EndPoint))
            {
                continue;
            }

            var peerEndPoint = IrohRaftTransport.CreateEndPoint(peer.EndPoint);
            if (members.Any(member => new IrohRaftEndPointComparer().Equals(member, peerEndPoint)))
            {
                continue;
            }

            members.Add(peerEndPoint);
            _logger.LogInformation("[Raft] Configured Iroh peer {PeerId} at {PeerEndPoint}.", peer.Id, peerEndPoint);
        }

        return members;
    }

    private void ConfigureInMemoryMembership(RaftCluster.NodeConfiguration configuration, IReadOnlyCollection<EndPoint> members)
    {
        var membership = configuration.UseInMemoryConfigurationStorage();
        var activeConfiguration = membership.CreateActiveConfigurationBuilder();
        foreach (var member in members)
        {
            activeConfiguration.Add(member);
        }

        activeConfiguration.Build();
    }

    private void ConfigurePersistentMembership(
        RaftCluster.NodeConfiguration configuration,
        IReadOnlyCollection<EndPoint> members,
        IEqualityComparer<EndPoint> comparer)
    {
        var membershipPath = Path.GetFullPath(_options.MembershipPath);
        Directory.CreateDirectory(membershipPath);

        PersistentEndPointClusterConfigurationStorage? runtimeStorage = null;
        try
        {
            InitializePersistentMembershipAsync(
                membershipPath,
                members,
                comparer,
                configuration.MemoryAllocator).GetAwaiter().GetResult();
            runtimeStorage = new PersistentEndPointClusterConfigurationStorage(
                membershipPath,
                Environment.SystemPageSize,
                comparer,
                configuration.MemoryAllocator);
            configuration.ConfigurationStorage = runtimeStorage;
            SaveMembershipManifest(membershipPath, members);
        }
        catch
        {
            runtimeStorage?.Dispose();
            throw;
        }

        _logger.LogInformation(
            "[Raft] Persisted configured cluster membership manifest at {MembershipPath} with {MemberCount} members.",
            membershipPath,
            members.Count);
    }

    private static async Task InitializePersistentMembershipAsync(
        string membershipPath,
        IReadOnlyCollection<EndPoint> configuredMembers,
        IEqualityComparer<EndPoint> comparer,
        MemoryAllocator<byte> allocator)
    {
        const int persistentHeaderSize = sizeof(long);
        string activePath = Path.Combine(membershipPath, "active.list");
        string proposedPath = Path.Combine(membershipPath, "proposed.list");
        long activeLength = File.Exists(activePath) ? new FileInfo(activePath).Length : 0L;
        long proposedLength = File.Exists(proposedPath) ? new FileInfo(proposedPath).Length : 0L;
        if (activeLength is > 0 and <= persistentHeaderSize ||
            proposedLength is > 0 and <= persistentHeaderSize)
        {
            throw new InvalidDataException(
                $"Raft membership storage at '{membershipPath}' contains a truncated configuration file.");
        }

        using var storage = new PersistentEndPointClusterConfigurationStorage(
            membershipPath,
            Environment.SystemPageSize,
            comparer,
            allocator);
        var typedStorage = (IClusterConfigurationStorage<EndPoint>)storage;
        var storageControl = (IClusterConfigurationStorage)storage;

        if (activeLength <= persistentHeaderSize)
        {
            if (proposedLength > persistentHeaderSize)
            {
                throw new InvalidOperationException(
                    "Raft membership storage contains a proposed configuration without an active configuration. Recover or clear the membership directory before startup.");
            }

            foreach (var member in configuredMembers)
            {
                if (await typedStorage.AddMemberAsync(member))
                {
                    await storageControl.ApplyAsync();
                }
            }

            return;
        }

        await storageControl.LoadConfigurationAsync();
        if (storage.HasProposal)
        {
            throw new InvalidOperationException(
                "Raft membership storage contains an interrupted proposed configuration. Recover or clear the membership directory before startup.");
        }

        bool configurationMatches = typedStorage.ActiveConfiguration.Count == configuredMembers.Count &&
                                    configuredMembers.All(member => typedStorage.ActiveConfiguration.Contains(member, comparer));
        if (!configurationMatches)
        {
            string persisted = string.Join(", ", typedStorage.ActiveConfiguration.Select(FormatEndpoint).Order());
            string configured = string.Join(", ", configuredMembers.Select(FormatEndpoint).Order());
            throw new InvalidOperationException(
                $"Configured Raft members do not match persisted membership. Persisted: [{persisted}]. Configured: [{configured}]. " +
                "Use a controlled membership change instead of editing peer configuration in place.");
        }
    }

    private void SaveMembershipManifest(string membershipPath, IReadOnlyCollection<EndPoint> members)
    {
        var manifestPath = Path.Combine(membershipPath, "membership.json");
        var manifest = new
        {
            _options.NodeId,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Members = members.Select(FormatEndpoint).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
        };

        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static EndPoint ParsePublicEndPoint(string value)
    {
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            var hostPort = value.Split(':', 2, StringSplitOptions.TrimEntries);
            if (hostPort.Length == 2 && int.TryParse(hostPort[1], out var hostPortValue))
            {
                return CreateEndPoint(hostPort[0], hostPortValue);
            }
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return CreateEndPoint(uri.Host, uri.Port);
        }

        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var port))
        {
            return CreateEndPoint(parts[0], port);
        }

        throw new InvalidOperationException($"Invalid Raft endpoint '{value}'. Use host:port or an absolute URI.");
    }

    private static EndPoint CreateEndPoint(string host, int port)
    {
        if (port <= 0)
        {
            throw new InvalidOperationException($"Invalid Raft endpoint port '{port}'.");
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return new IPEndPoint(IPAddress.Loopback, port);
        }

        if (string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase))
        {
            return new IPEndPoint(IPAddress.IPv6Loopback, port);
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return new IPEndPoint(address, port);
        }

        return new DnsEndPoint(host, port);
    }

    private static IPEndPoint ToBindEndPoint(EndPoint publicEndPoint)
    {
        return publicEndPoint switch
        {
            IPEndPoint ip when IPAddress.IsLoopback(ip.Address) => ip,
            IPEndPoint ip when ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 => new IPEndPoint(IPAddress.IPv6Any, ip.Port),
            IPEndPoint ip => new IPEndPoint(IPAddress.Any, ip.Port),
            DnsEndPoint dns when IsLocalHost(dns.Host) => new IPEndPoint(IPAddress.Loopback, dns.Port),
            DnsEndPoint dns => new IPEndPoint(IPAddress.Any, dns.Port),
            _ => throw new InvalidOperationException($"Unsupported Raft endpoint type '{publicEndPoint.GetType().Name}'.")
        };
    }

    private static bool IsLocalHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreEquivalentEndpoints(EndPoint left, EndPoint right)
    {
        return FormatEndpoint(left).Equals(FormatEndpoint(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatEndpoint(EndPoint endPoint)
    {
        return endPoint switch
        {
            IPEndPoint ip => $"{ip.Address}:{ip.Port}",
            DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
            _ => endPoint.ToString() ?? string.Empty
        };
    }

    private sealed class EndPointEqualityComparer : IEqualityComparer<EndPoint>
    {
        public bool Equals(EndPoint? x, EndPoint? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x == null || y == null)
            {
                return false;
            }

            return AreEquivalentEndpoints(x, y);
        }

        public int GetHashCode(EndPoint obj)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(FormatEndpoint(obj));
        }
    }

    private sealed class PersistentEndPointClusterConfigurationStorage : PersistentClusterConfigurationStorage<EndPoint>
    {
        public PersistentEndPointClusterConfigurationStorage(
            string path,
            int fileBufferSize,
            IEqualityComparer<EndPoint> comparer,
            MemoryAllocator<byte> allocator)
            : base(path, fileBufferSize, comparer, allocator)
        {
        }

        protected override void Encode(EndPoint address, ref BufferWriterSlim<byte> output)
        {
            output.WriteEndPoint(address);
        }

        protected override EndPoint Decode(ref SequenceReader input)
        {
            return input.ReadEndPoint();
        }
    }
}

#pragma warning restore DOTNEXT001
