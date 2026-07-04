using Blockchain.Application.Analytics;
using Blockchain.Application.Git;
using Blockchain.Application.Projects;
using Blockchain.Core;
using Blockchain.Core.Contracts;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Blockchain.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNexusInfrastructure(this IServiceCollection services, DatabaseManager databaseManager)
    {
        services.AddSingleton(databaseManager);

        services.AddSingleton<SqliteBlockStore>();
        services.AddSingleton<IBlockchainStore>(sp => sp.GetRequiredService<SqliteBlockStore>());
        services.AddSingleton<IBlockStore>(sp => sp.GetRequiredService<SqliteBlockStore>());
        services.AddSingleton<IChainReader>(sp => sp.GetRequiredService<SqliteBlockStore>());
        services.AddSingleton<IChainWriter>(sp => sp.GetRequiredService<SqliteBlockStore>());
        services.AddSingleton<IPendingBlockStore>(sp => sp.GetRequiredService<SqliteBlockStore>());
        services.AddSingleton<IBlockFinalityMetadataStore>(sp => sp.GetRequiredService<SqliteBlockStore>());

        services.AddSingleton<SqliteProjectMembershipStore>();
        services.AddSingleton<IUserProjectReader>(sp => sp.GetRequiredService<SqliteProjectMembershipStore>());
        services.AddSingleton<IProjectMembershipStore>(sp => sp.GetRequiredService<SqliteProjectMembershipStore>());

        services.AddSingleton<SqliteSmartContractStateReader>();
        services.AddSingleton<ISmartContractStateReader>(sp => sp.GetRequiredService<SqliteSmartContractStateReader>());
        services.AddSingleton<ISmartContractState>(sp => sp.GetRequiredService<SqliteSmartContractStateReader>());

        services.AddSingleton<SqlitePeerStore>();
        services.AddSingleton<IPeerStore>(sp => sp.GetRequiredService<SqlitePeerStore>());

        services.AddSingleton<SqliteTaskProjectionStore>();
        services.AddSingleton<SqliteDocumentStore>();
        services.AddSingleton<SqliteGovernanceStore>();
        services.AddSingleton<IReplayStoreFactory, DatabaseReplayStoreFactory>();

        services.AddSingleton<IProjectMembershipReader, DatabaseProjectMembershipReader>();
        services.AddSingleton<ISignatureVerifier, EcdsaSignatureVerifier>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddSingleton<IProjectAnalyticsReader, ProjectAnalyticsReader>();
        services.AddSingleton<IBlockAuditVerifier, CoreBlockAuditVerifier>();

        services.AddSingleton<ProjectReadConnectionFactory>();
        services.AddSingleton<IProjectTaskReader, ProjectTaskReader>();
        services.AddSingleton<IGovernanceReader, GovernanceReader>();
        services.AddSingleton<IDocumentReader, DocumentReader>();

        return services;
    }
}
