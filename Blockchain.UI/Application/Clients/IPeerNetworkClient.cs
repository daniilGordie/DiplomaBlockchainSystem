namespace Blockchain.UI.Application.Clients;

public interface IPeerNetworkClient
{
    Task<PeerDirectorySnapshot> GetPeerDirectoryAsync(string nodeUrl);
    Task<NodeSetupStatus?> GetSetupStatusAsync(string nodeUrl);
    Task<NetworkStatus?> GetNetworkStatusAsync(string nodeUrl);
    Task<NetworkInvite?> GetNetworkInviteAsync(string nodeUrl);
    Task<IntentListStatus?> GetIntentListAsync(string nodeUrl);
    Task<MigrationChecklist?> GetMigrationChecklistAsync(string nodeUrl);
    Task<SetupPlanResponse?> CreateSetupPlanAsync(string nodeUrl, SetupPlanRequest request);
    Task<SetupStateStatus?> GetSetupStateAsync(string nodeUrl);
    Task<InviteValidationResponse?> ValidateInviteAsync(string nodeUrl, InviteValidationRequest request);
    Task<SetupApplyResponse?> CreateNetworkAsync(string nodeUrl, CreateNetworkSetupRequest request);
    Task<SetupApplyResponse?> JoinNetworkAsync(string nodeUrl, JoinNetworkSetupRequest request);
    Task<SetupApplyResponse?> CreateLocalNodeAsync(string nodeUrl, LocalNodeSetupRequest request);
    Task<PeerTrustResponse?> SetPeerTrustAsync(string nodeUrl, PeerTrustRequest request);
    Task<PeerRoleResponse?> SetPeerRoleAsync(string nodeUrl, PeerRoleRequest request);
    Task<EdgeSyncStatus?> SyncNetworkAsync(string nodeUrl, NetworkSyncRequest request);
    Task<UpdateCheckStatus?> GetUpdateCheckAsync(string nodeUrl);
}
