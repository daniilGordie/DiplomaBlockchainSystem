using Blockchain.Application.Analytics;
using Blockchain.Application.Artifacts;
using Blockchain.Application.Blocks;
using Blockchain.Application.Git;
using Blockchain.Application.Projects;
using Blockchain.Application.Security;
using Blockchain.Core;
using Blockchain.Core.Consensus;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Blockchain.Infrastructure;
using Blockchain.Infrastructure.Persistence;

namespace Blockchain.Node.Services;

#pragma warning disable DOTNEXT001

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNexusNodeServices(this IServiceCollection services, DatabaseManager databaseManager)
    {
        services.AddOptions<P2POptions>().BindConfiguration("P2P");
        services.AddOptions<NodeVersionOptions>().BindConfiguration("NodeVersion");
        services.AddOptions<ConsensusOptions>().BindConfiguration("Consensus");
        services.AddOptions<RaftOptions>().BindConfiguration("Raft");
        services.AddHttpClient();
        services.AddNexusInfrastructure(databaseManager);

        services.AddSingleton<BlockMiner>();
        services.AddSingleton<PendingBlockConnector>();
        services.AddSingleton<PeerBlockValidator>();
        services.AddSingleton<ChainAdoptionService>();
        services.AddSingleton<ContributionScoreService>();
        services.AddSingleton<ProducerSelector>();
        services.AddSingleton<BlockProposalFactory>();
        services.AddSingleton(sp => new PoCVerifier(
            sp.GetRequiredService<ContributionScoreService>(),
            sp.GetRequiredService<ProducerSelector>()));
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
        services.AddSingleton<NodeIdentity>();
        services.AddSingleton<PeerRegistrationSecurity>();
        services.AddHttpClient<IrohSidecarClient>();
        services.AddHostedService<NodeIdentityWarmupService>();
        services.AddHostedService<P2PBootstrapService>();
        services.AddHostedService<IrohInboundPump>();
        services.AddSingleton<OracleIdentity>();
        services.AddSingleton<ProducerIdentity>();
        services.AddSingleton<ProjectEventAnchorService>();
        services.AddSingleton<BlockNotificationService>();
        services.AddSingleton<CommittedBlockApplier>();
        services.AddSingleton<RaftCommittedBlockCommandApplier>();
        services.AddSingleton<RaftBlockStateMachine>();
        services.AddSingleton<IStateMachine>(sp => sp.GetRequiredService<RaftBlockStateMachine>());
        services.AddSingleton<DotNextRaftClusterFactory>();
        services.AddSingleton<RaftCluster>(sp => sp.GetRequiredService<DotNextRaftClusterFactory>().CreateCluster());
        services.AddSingleton<IRaftCluster>(sp => sp.GetRequiredService<RaftCluster>());
        services.AddSingleton<IRaftCommandReplicator, DotNextRaftCommandReplicator>();
        services.AddHostedService<DotNextRaftClusterHostedService>();
        services.AddSingleton<IBlockFinalitySubmitter>(sp =>
        {
            var mode = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ConsensusOptions>>().Value.FinalityMode;
            if (string.Equals(mode, ConsensusFinalityModes.Immediate, StringComparison.OrdinalIgnoreCase))
            {
                return ActivatorUtilities.CreateInstance<ImmediateBlockFinalitySubmitter>(sp);
            }

            if (string.Equals(mode, ConsensusFinalityModes.Raft, StringComparison.OrdinalIgnoreCase))
            {
                var raftOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RaftOptions>>().Value;
                if (!raftOptions.HasMinimumConfiguration)
                {
                    throw new InvalidOperationException("Consensus:FinalityMode=Raft requires Raft:NodeId, Raft:PublicEndPoint, and at least one Raft:Peers entry.");
                }

                return ActivatorUtilities.CreateInstance<RaftBlockFinalitySubmitter>(sp);
            }

            throw new InvalidOperationException($"Unsupported Consensus:FinalityMode '{mode}'.");
        });
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
        services.AddSingleton<VerifyBlockProposalUseCase>();

        return services;
    }
}

#pragma warning restore DOTNEXT001
