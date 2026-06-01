using Blockchain.Application.Analytics;
using Blockchain.Application.Artifacts;
using Blockchain.Application.Blocks;
using Blockchain.Application.Git;
using Blockchain.Application.Projects;
using Blockchain.Application.Security;
using Blockchain.Core;
using Blockchain.Infrastructure;
using Blockchain.Infrastructure.Persistence;

namespace Blockchain.Node.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNexusNodeServices(this IServiceCollection services, DatabaseManager databaseManager)
    {
        services.AddOptions<P2POptions>().BindConfiguration("P2P");
        services.AddNexusInfrastructure(databaseManager);

        services.AddSingleton<BlockMiner>();
        services.AddSingleton<PendingBlockConnector>();
        services.AddSingleton<PeerBlockValidator>();
        services.AddSingleton<ChainAdoptionService>();
        services.AddSingleton(sp => new BlockchainManager(
            sp.GetRequiredService<IChainReader>(),
            sp.GetRequiredService<IChainWriter>(),
            sp.GetRequiredService<IPendingBlockStore>(),
            sp.GetRequiredService<Blockchain.Core.Contracts.ISmartContractStateReader>(),
            sp.GetRequiredService<IReplayStoreFactory>(),
            sp.GetRequiredService<BlockMiner>(),
            sp.GetRequiredService<PendingBlockConnector>(),
            sp.GetRequiredService<PeerBlockValidator>(),
            sp.GetRequiredService<ChainAdoptionService>()));

        services.AddSingleton<P2PNetworkService>();
        services.AddHostedService<P2PBootstrapService>();
        services.AddSingleton<OracleIdentity>();
        services.AddSingleton<ProjectEventAnchorService>();
        services.AddSingleton<GrpcBlockProcessor>();
        services.AddSingleton<PeerChainSyncService>();
        services.AddSingleton<ProjectResponseCache>();
        services.AddSingleton<WebhookReplayGuard>();
        services.AddSingleton<IRequestReplayGuard>(sp => sp.GetRequiredService<WebhookReplayGuard>());
        services.AddSingleton<GitProjectBindingStore>();
        services.AddSingleton<IGitRepositoryBindingStore>(sp => sp.GetRequiredService<GitProjectBindingStore>());

        services.AddSingleton<ConnectGitRepositoryUseCase>();
        services.AddSingleton<AnchorGitCommitUseCase>();
        services.AddSingleton<AnchorArtifactUseCase>();
        services.AddSingleton<AuthorizeReadRequestUseCase>();
        services.AddSingleton<ProjectAccessPolicy>();
        services.AddSingleton<ProjectReadAccessGuard>();
        services.AddSingleton<ChainReadAccessGuard>();
        services.AddSingleton<UserReadAccessGuard>();

        services.AddSingleton<GetProjectAnalyticsUseCase>();
        services.AddSingleton<GetSecurityAuditUseCase>();

        services.AddSingleton<GetProjectTasksUseCase>();
        services.AddSingleton<GetTaskHistoryUseCase>();
        services.AddSingleton<GetGovernanceProposalsUseCase>();
        services.AddSingleton<GetProjectDocumentsUseCase>();
        services.AddSingleton<GetDocumentVersionsUseCase>();
        services.AddSingleton<BroadcastLocalBlockUseCase>();
        services.AddSingleton<ReceivePeerBlockUseCase>();
        services.AddSingleton<AdoptPeerChainUseCase>();
        services.AddSingleton<MineAndAppendBlockUseCase>();

        return services;
    }
}
