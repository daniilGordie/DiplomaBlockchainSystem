namespace Blockchain.UI.Application.Clients;

public sealed record PeerNetworkOverview(
    PeerDirectorySnapshot Directory,
    NodeSetupStatus? SetupStatus,
    NetworkStatus? NetworkStatus,
    NetworkInvite? NetworkInvite,
    IntentListStatus? IntentList,
    MigrationChecklist? MigrationChecklist,
    UpdateCheckStatus? UpdateCheck);
