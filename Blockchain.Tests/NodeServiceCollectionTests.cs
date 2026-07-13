using Blockchain.Application.Artifacts;
using Blockchain.Application.Git;
using Blockchain.Application.Security;
using Blockchain.Core;
using Blockchain.Core.Consensus;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Node.Services;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

#pragma warning disable DOTNEXT001

public class NodeServiceCollectionTests
{
    [Fact]
    public async Task AddNexusNodeServices_RegistersApplicationPorts()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexus-di-{Guid.NewGuid():N}.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NodeDbPassword"] = "test-password",
                    ["NodeDatabase"] = dbPath
                })
                .Build();

            var database = new DatabaseManager(dbPath, "test-password");
            var services = new ServiceCollection();

            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
            services.AddLogging();
            services.AddSignalR();
            services.AddNexusNodeServices(database);

            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            Assert.NotNull(provider.GetRequiredService<IGitRepositoryBindingStore>());
            Assert.NotNull(provider.GetRequiredService<IRequestReplayGuard>());
            Assert.NotNull(provider.GetRequiredService<IBlockchainStore>());
            Assert.NotNull(provider.GetRequiredService<IBlockStore>());
            Assert.NotNull(provider.GetRequiredService<IChainReader>());
            Assert.NotNull(provider.GetRequiredService<IChainWriter>());
            Assert.NotNull(provider.GetRequiredService<IPendingBlockStore>());
            Assert.NotNull(provider.GetRequiredService<IUserProjectReader>());
            Assert.NotNull(provider.GetRequiredService<IProjectMembershipStore>());
            Assert.NotNull(provider.GetRequiredService<IPeerStore>());
            Assert.NotNull(provider.GetRequiredService<ContributionScoreService>());
            Assert.NotNull(provider.GetRequiredService<ProducerSelector>());
            Assert.NotNull(provider.GetRequiredService<BlockProposalFactory>());
            Assert.NotNull(provider.GetRequiredService<PoCVerifier>());
            Assert.NotNull(provider.GetRequiredService<BlockNotificationService>());
            Assert.NotNull(provider.GetRequiredService<CommittedBlockApplier>());
            Assert.NotNull(provider.GetRequiredService<RaftCommittedBlockCommandApplier>());
            Assert.NotNull(provider.GetRequiredService<IStateMachine>());
            Assert.NotNull(provider.GetRequiredService<IBlockFinalitySubmitter>());
            Assert.NotNull(provider.GetRequiredService<ProjectEventAnchorService>());
            Assert.NotNull(provider.GetRequiredService<ConnectGitRepositoryUseCase>());
            Assert.NotNull(provider.GetRequiredService<AnchorGitCommitUseCase>());
            Assert.NotNull(provider.GetRequiredService<AnchorArtifactUseCase>());
            Assert.NotNull(provider.GetRequiredService<AuthorizeReadRequestUseCase>());
            Assert.NotNull(ActivatorUtilities.CreateInstance<BlockchainGrpcService>(provider));
        }
        finally
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
            catch (IOException)
            {
                // SQLite may keep a pooled handle briefly on Windows after DI validation.
            }
        }
    }

    [Fact]
    public async Task AddNexusNodeServices_ShouldResolveRaftClusterWhenRaftModeIsConfigured()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexus-di-raft-{Guid.NewGuid():N}.db");
        var raftLogPath = Path.Combine(Path.GetTempPath(), $"nexus-raft-{Guid.NewGuid():N}");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NodeDbPassword"] = "test-password",
                    ["NodeDatabase"] = dbPath,
                    ["Consensus:FinalityMode"] = "Raft",
                    ["Raft:NodeId"] = "node-a",
                    ["Raft:PublicEndPoint"] = "http://localhost:6041",
                    ["Raft:LogPath"] = raftLogPath,
                    ["Raft:Peers:0:Id"] = "node-b",
                    ["Raft:Peers:0:EndPoint"] = "http://localhost:6042"
                })
                .Build();

            var database = new DatabaseManager(dbPath, "test-password");
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
            services.AddLogging();
            services.AddSignalR();
            services.AddNexusNodeServices(database);

            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            Assert.NotNull(provider.GetRequiredService<DotNext.Net.Cluster.Consensus.Raft.IRaftCluster>());
            Assert.IsAssignableFrom<SimpleStateMachine>(provider.GetRequiredService<IStateMachine>());
            Assert.IsType<RaftBlockFinalitySubmitter>(provider.GetRequiredService<IBlockFinalitySubmitter>());
        }
        finally
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }

                if (Directory.Exists(raftLogPath))
                {
                    Directory.Delete(raftLogPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // SQLite may keep a pooled handle briefly on Windows after DI validation.
            }
        }
    }

    [Fact]
    public async Task AddNexusNodeServices_ShouldAllowEdgeNodeWithoutLocalRaftConfiguration()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nexus-di-edge-{Guid.NewGuid():N}.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NodeDbPassword"] = "test-password",
                    ["NodeDatabase"] = dbPath,
                    ["Node:Role"] = "Edge",
                    ["P2P:Iroh:Enabled"] = "true",
                    ["P2P:Iroh:LocalApiToken"] = "test-iroh-token",
                    ["Consensus:FinalityMode"] = "Raft"
                })
                .Build();

            var database = new DatabaseManager(dbPath, "test-password");
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
            services.AddLogging();
            services.AddSignalR();
            services.AddNexusNodeServices(database);

            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            Assert.IsType<EdgeBlockFinalitySubmitter>(provider.GetRequiredService<IBlockFinalitySubmitter>());
        }
        finally
        {
            try
            {
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
            catch (IOException)
            {
                // SQLite may keep a pooled handle briefly on Windows after DI validation.
            }
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
        }
    }
}

#pragma warning restore DOTNEXT001
