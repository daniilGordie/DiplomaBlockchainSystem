# Defense Architecture Map

## End-to-end flows

### 1) UI -> API -> Blockchain -> State

1. `BlockchainDashboard` builds signed payload (`TaskEvent` / governance / docs / artifacts).
2. gRPC `ReceiveBlock` verifies ECDSA signature and smart-contract policy.
3. `BlockchainManager.ProcessPeerBlock` validates PoW, channel consistency, contract execution.
4. `DatabaseManager.SaveBlock` persists block and updates state indexes (`Tasks`, `ProjectMembers`, `DocumentVersions`, governance tables).

Main files:

- `Blockchain.UI/Components/Pages/BlockchainDashboard.razor.cs`
- `Blockchain.Node/Services/BlockchainGrpcService.cs`
- `Blockchain.Core/BlockchainManager.cs`
- `Blockchain.Core/DatabaseManager.cs`

### 2) User action -> Audit trail -> Blockchain evidence

1. User action creates blockchain event.
2. Block hash/index/timestamp are persisted in chain tables.
3. Analytics tab rebuilds and shows recent immutable audit trail rows.

Main files:

- `Blockchain.UI/Components/Dashboard/AnalyticsTab.razor`
- `Blockchain.UI/Components/Pages/BlockchainDashboard.razor.cs` (`RebuildAuditTrail`)

### 3) Git webhook -> Server anchoring -> P2P/Realtime

1. Node receives `/api/webhooks/git`.
2. Verifies HMAC and replay-protects request.
3. Node anchors commit into project channel with oracle signature.
4. Broadcasts block to peers and SignalR subscribers.

Main files:

- `Blockchain.Node/Program.cs`
- `Blockchain.Node/Services/WebhookReplayGuard.cs`
- `Blockchain.Node/Services/P2PNetworkService.cs`
- `Blockchain.Node/Hubs/BlockChainHub.cs`

### 4) P2P sync -> Secure chain adoption

1. Node fetches channels/chains from peer.
2. Candidate chain is structurally verified.
3. Candidate is fully replayed in temporary DB through contracts.
4. Only valid replay is adopted and indexes rebuilt.

Main files:

- `Blockchain.Node/Services/BlockchainGrpcService.cs`
- `Blockchain.Core/BlockchainManager.cs`

## Security controls summary

- Signed writes (ECDSA P-256, P1363 format).
- Signed project-scoped reads (user key binding).
- RBAC smart contract enforcement.
- Oracle-signed trusted feed events.
- Channel/payload consistency guard.
- Webhook HMAC + replay guard.
- SignalR group join authorization by project membership.
- Peer management via `NodeAdminToken`.
