using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Node;
using Blockchain.Node.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public class P2PNetworkDiscoveryTests
{
    [Fact]
    public async Task RegisterPeer_ShouldAcceptValidFullNode()
    {
        string dbPath = TempDbPath("p2p-register-valid");
        try
        {
            var service = CreateGrpcService(dbPath, new Dictionary<string, string?>
            {
                ["P2P:NodeRole"] = "Bootstrap",
                ["P2P:NodeId"] = "bootstrap",
                ["P2P:PublicUrl"] = "https://bootstrap.example.test",
                ["P2P:RegistrationToken"] = "registration-secret",
                ["P2P:SyncToken"] = "sync-secret"
            });

            var result = await service.RegisterPeer(new RegisterPeerRequest
            {
                NodeId = "node-a",
                PublicUrl = "https://node-a.example.test",
                Role = "Full",
                RegistrationToken = "registration-secret"
            }, null!);

            var directory = await service.GetPeerDirectory(new EmptyRequest(), null!);

            Assert.True(result.Success);
            var peer = Assert.Single(directory.Peers);
            Assert.Equal("node-a", peer.NodeId);
            Assert.Equal("https://node-a.example.test", peer.PublicUrl);
            Assert.Equal("Full", peer.Role);
            Assert.True(peer.IsTrusted);
            Assert.False(string.IsNullOrWhiteSpace(peer.LastSeen));
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    [Fact]
    public async Task RegisterPeer_ShouldRejectInvalidRegistrationToken()
    {
        string dbPath = TempDbPath("p2p-register-token");
        try
        {
            var service = CreateGrpcService(dbPath, new Dictionary<string, string?>
            {
                ["P2P:NodeRole"] = "Bootstrap",
                ["P2P:PublicUrl"] = "https://bootstrap.example.test",
                ["P2P:RegistrationToken"] = "registration-secret",
                ["P2P:SyncToken"] = "sync-secret"
            });

            var result = await service.RegisterPeer(new RegisterPeerRequest
            {
                NodeId = "node-a",
                PublicUrl = "https://node-a.example.test",
                Role = "Full",
                RegistrationToken = "wrong"
            }, null!);

            var directory = await service.GetPeerDirectory(new EmptyRequest(), null!);

            Assert.False(result.Success);
            Assert.Empty(directory.Peers);
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    [Fact]
    public async Task RegisterPeer_ShouldRejectLocalhostInPublicMode()
    {
        string dbPath = TempDbPath("p2p-register-localhost");
        try
        {
            var service = CreateGrpcService(dbPath, new Dictionary<string, string?>
            {
                ["P2P:NodeRole"] = "Bootstrap",
                ["P2P:PublicUrl"] = "https://bootstrap.example.test",
                ["P2P:RegistrationToken"] = "registration-secret",
                ["P2P:SyncToken"] = "sync-secret"
            });

            var result = await service.RegisterPeer(new RegisterPeerRequest
            {
                NodeId = "node-local",
                PublicUrl = "http://localhost:7001",
                Role = "Full",
                RegistrationToken = "registration-secret"
            }, null!);

            Assert.False(result.Success);
            Assert.Equal("Invalid public URL", result.Message);
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    [Fact]
    public void PeerStore_ShouldPersistPeerMetadata()
    {
        string dbPath = TempDbPath("p2p-peer-store");
        try
        {
            var database = new DatabaseManager(dbPath, "");
            database.SavePeer(new PeerInfo("https://node-a.example.test", "node-a", "Full"));
            database.MarkPeerFailure("https://node-a.example.test");

            var peer = Assert.Single(database.LoadPeerInfos());

            Assert.Equal("node-a", peer.NodeId);
            Assert.Equal("Full", peer.Role);
            Assert.False(string.IsNullOrWhiteSpace(peer.LastSeen));
            Assert.False(string.IsNullOrWhiteSpace(peer.LastFailure));
            Assert.True(peer.IsTrusted);
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    private static BlockchainGrpcService CreateGrpcService(string dbPath, Dictionary<string, string?> p2pSettings)
    {
        var values = new Dictionary<string, string?>(p2pSettings)
        {
            ["ConnectionStrings:DefaultNodeDb"] = dbPath,
            ["NodeDbPassword"] = "test-db-password",
            ["NodeAdminToken"] = "admin-secret",
            ["OraclePublicKey"] = "test-oracle",
            ["WebhookSecret"] = "test-webhook"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var database = new DatabaseManager(dbPath, "");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSignalR();
        services.AddNexusNodeServices(database);

        var provider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<BlockchainGrpcService>(provider);
    }

    private static string TempDbPath(string prefix) => Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.db");

    private static void DeleteDbFiles(string dbPath)
    {
        TryDelete(dbPath);
        TryDelete(dbPath + "-wal");
        TryDelete(dbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for SQLite temp files.
        }
    }
}
