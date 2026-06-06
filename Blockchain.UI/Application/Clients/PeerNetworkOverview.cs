namespace Blockchain.UI.Application.Clients;

public sealed record PeerNetworkOverview(
    PeerDirectorySnapshot Directory,
    NodeSetupStatus? SetupStatus,
    MigrationChecklist? MigrationChecklist,
    UpdateCheckStatus? UpdateCheck);
