namespace Blockchain.UI.Application.Clients;

public interface IPeerNetworkClient
{
    Task<PeerDirectorySnapshot> GetPeerDirectoryAsync(string nodeUrl);
    Task<NodeSetupStatus?> GetSetupStatusAsync(string nodeUrl);
    Task<MigrationChecklist?> GetMigrationChecklistAsync(string nodeUrl);
    Task<SetupPlanResponse?> CreateSetupPlanAsync(string nodeUrl, SetupPlanRequest request);
    Task<UpdateCheckStatus?> GetUpdateCheckAsync(string nodeUrl);
}
