using DotNext.Buffers;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Blockchain.Node.Services;

#pragma warning disable DOTNEXT001

public sealed class DotNextRaftClusterFactory
{
    private readonly RaftOptions _options;
    private readonly IStateMachine _stateMachine;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DotNextRaftClusterFactory> _logger;

    public DotNextRaftClusterFactory(
        IOptions<RaftOptions> options,
        IStateMachine stateMachine,
        ILoggerFactory loggerFactory,
        ILogger<DotNextRaftClusterFactory> logger)
    {
        _options = options.Value;
        _stateMachine = stateMachine;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public RaftCluster CreateCluster()
    {
        if (!_options.HasMinimumConfiguration)
        {
            throw new InvalidOperationException("Raft cluster requires Raft:NodeId, Raft:PublicEndPoint, and at least one Raft:Peers entry.");
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
            ConfigurePersistentMembership(configuration, configuredMembers);
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

    private void ConfigureInMemoryMembership(RaftCluster.TcpConfiguration configuration, IReadOnlyCollection<EndPoint> members)
    {
        var membership = configuration.UseInMemoryConfigurationStorage();
        var activeConfiguration = membership.CreateActiveConfigurationBuilder();
        foreach (var member in members)
        {
            activeConfiguration.Add(member);
        }

        activeConfiguration.Build();
    }

    private void ConfigurePersistentMembership(RaftCluster.TcpConfiguration configuration, IReadOnlyCollection<EndPoint> members)
    {
        var membershipPath = Path.GetFullPath(_options.MembershipPath);
        Directory.CreateDirectory(membershipPath);

        ConfigureInMemoryMembership(configuration, members);
        SaveMembershipManifest(membershipPath, members);

        _logger.LogInformation(
            "[Raft] Persisted configured cluster membership manifest at {MembershipPath} with {MemberCount} members.",
            membershipPath,
            members.Count);
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
            IPEndPoint ip => ip,
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
            output.Write(Encoding.UTF8.GetBytes(FormatEndpoint(address)));
        }

        protected override EndPoint Decode(ref SequenceReader input)
        {
            ReadOnlySequence<byte> bytes = input.ReadToEnd();
            return ParsePublicEndPoint(Encoding.UTF8.GetString(bytes.ToArray()));
        }
    }
}

#pragma warning restore DOTNEXT001
