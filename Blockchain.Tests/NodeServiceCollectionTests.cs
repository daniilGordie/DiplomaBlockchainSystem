using Blockchain.Application.Artifacts;
using Blockchain.Application.Git;
using Blockchain.Application.Security;
using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Node.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public class NodeServiceCollectionTests
{
    [Fact]
    public void AddNexusNodeServices_RegistersApplicationPorts()
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

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
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
