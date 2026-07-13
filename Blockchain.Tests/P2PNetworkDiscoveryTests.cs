using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Node;
using Blockchain.Node.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;

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

            var result = await service.RegisterPeer(CreateSignedRegistration(
                "node-a",
                "https://node-a.example.test",
                "Full"), null!);

            var directory = await service.GetPeerDirectory(new EmptyRequest(), null!);
            var registry = new DatabaseManager(dbPath, "").LoadAllPeerInfos();

            Assert.True(result.Success);
            Assert.Empty(directory.Peers);
            var peer = Assert.Single(registry);
            Assert.Equal("node-a", peer.NodeId);
            Assert.Equal("https://node-a.example.test", peer.Url);
            Assert.Equal("Full", peer.Role);
            Assert.False(peer.IsTrusted);
            Assert.False(string.IsNullOrWhiteSpace(peer.NodePublicKey));
            Assert.False(string.IsNullOrWhiteSpace(peer.LastSeen));
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    [Fact]
    public async Task RegisterPeer_ShouldAcceptIrohPeerRecordOnBootstrapNode()
    {
        string dbPath = TempDbPath("p2p-register-iroh");
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

            var result = await service.RegisterPeer(CreateSignedRegistration(
                "iroh-node-a",
                "iroh://2jc4u57t7y4wuwcany4oa7enrhnnj3cfom3t7fhrc27fdvxfpv3q",
                "Full"), null!);

            var directory = await service.GetPeerDirectory(new EmptyRequest(), null!);
            var registry = new DatabaseManager(dbPath, "").LoadAllPeerInfos();

            Assert.True(result.Success);
            Assert.Empty(directory.Peers);
            var peer = Assert.Single(registry);
            Assert.Equal("iroh://2jc4u57t7y4wuwcany4oa7enrhnnj3cfom3t7fhrc27fdvxfpv3q", peer.Url);
            Assert.False(peer.IsTrusted);
        }
        finally
        {
            DeleteDbFiles(dbPath);
        }
    }

    [Fact]
    public async Task RegisterPeer_ShouldRejectUnsignedLegacyTokenByDefault()
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
                RegistrationToken = "registration-secret"
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
    public async Task RegisterPeer_ShouldRejectInvalidSignature()
    {
        string dbPath = TempDbPath("p2p-register-invalid-signature");
        try
        {
            var service = CreateGrpcService(dbPath, new Dictionary<string, string?>
            {
                ["P2P:NodeRole"] = "Bootstrap",
                ["P2P:PublicUrl"] = "https://bootstrap.example.test",
                ["P2P:SyncToken"] = "sync-secret"
            });

            var request = CreateSignedRegistration("node-a", "https://node-a.example.test", "Full");
            request.Signature = Convert.ToBase64String(new byte[64]);

            var result = await service.RegisterPeer(request, null!);
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
    public async Task RegisterPeer_ShouldAllowLegacyTokenOnlyWhenExplicitlyEnabled()
    {
        string dbPath = TempDbPath("p2p-register-token-fallback");
        try
        {
            var service = CreateGrpcService(dbPath, new Dictionary<string, string?>
            {
                ["P2P:NodeRole"] = "Bootstrap",
                ["P2P:PublicUrl"] = "https://bootstrap.example.test",
                ["P2P:RegistrationToken"] = "registration-secret",
                ["P2P:AllowRegistrationTokenFallback"] = "true",
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
            var registry = new DatabaseManager(dbPath, "").LoadAllPeerInfos();

            Assert.True(result.Success);
            Assert.Empty(directory.Peers);
            var peer = Assert.Single(registry);
            Assert.False(peer.IsTrusted);
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

            var result = await service.RegisterPeer(CreateSignedRegistration(
                "node-local",
                "http://localhost:7001",
                "Full"), null!);

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

    [Fact]
    public void PeerStore_ShouldFilterUntrustedPeersUntilApproved()
    {
        string dbPath = TempDbPath("p2p-peer-trust");
        try
        {
            var database = new DatabaseManager(dbPath, "");
            database.SavePeer(new PeerInfo("https://node-a.example.test", "node-a", "Full", IsTrusted: false));

            Assert.Empty(database.LoadPeerInfos());
            var pending = Assert.Single(database.LoadAllPeerInfos());
            Assert.False(pending.IsTrusted);

            database.SetPeerTrust("https://node-a.example.test", true);
            var trusted = Assert.Single(database.LoadPeerInfos());
            Assert.True(trusted.IsTrusted);

            database.SetPeerRole("https://node-a.example.test", "Consensus");
            trusted = Assert.Single(database.LoadPeerInfos());
            Assert.Equal("Consensus", trusted.Role);

            database.SetPeerTrust("https://node-a.example.test", false);
            Assert.Empty(database.LoadPeerInfos());
            var revoked = Assert.Single(database.LoadAllPeerInfos());
            Assert.False(revoked.IsTrusted);
            Assert.Equal("Consensus", revoked.Role);
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

    private static RegisterPeerRequest CreateSignedRegistration(string nodeId, string publicUrl, string role)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        string signedAt = DateTimeOffset.UtcNow.ToString("O");
        string nonce = Guid.NewGuid().ToString("N");
        string payload = NodeIdentity.BuildRegistrationPayload(nodeId, publicUrl, role, signedAt, nonce);
        string signature = Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(payload),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        return new RegisterPeerRequest
        {
            NodeId = nodeId,
            PublicUrl = publicUrl,
            Role = role,
            NodePublicKey = publicKey,
            Signature = signature,
            SignedAt = signedAt,
            Nonce = nonce
        };
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
