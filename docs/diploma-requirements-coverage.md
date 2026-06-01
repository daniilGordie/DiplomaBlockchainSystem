# Diploma Requirements Coverage

This document maps the diploma plan requirements to the current implementation and gives a short demo path for defense preparation.

## Implemented Scope

| Plan requirement | Current coverage | Main implementation points |
| --- | --- | --- |
| Decentralized task management model | Implemented as a multi-project task board backed by blockchain events. | `Blockchain.UI` dashboard, `Blockchain.Node` gRPC API, `Blockchain.Core` block/state services |
| Blockchain-backed task lifecycle | Implemented for project creation, task creation, task moves, assignment, completion, and history. | `DatabaseManager.SaveBlock`, project channel tables, `GetTaskHistory` |
| Smart-contract access control | Implemented for project owners, members, task operations, governance proposals, and votes. | `AccessControllContract.Validate` |
| Frontend verification | Implemented component-level UI tests for analytics audit trail and team key-binding inputs. | `Blockchain.UI.Tests` (bUnit) |
| Signed read access for project data | Implemented for project-scoped gRPC reads (chain/tasks/history/governance/documents/analytics/audit) and user project-list reads. The node verifies the signed read scope, timestamp, nonce, and bound public key. | `BlockchainGrpcService.IsAuthorizedReadRequest`, UI request signatures |
| Realtime access control | Implemented: SignalR project-group subscription now checks authenticated connection identity and project membership. | `BlockchainHub.RegisterUser`, `BlockchainHub.JoinProject` |
| Project isolation | Implemented by using sanitized project-specific block tables and project-scoped history reads. | `DatabaseManager.LoadChain`, `BlockchainGrpcService.GetTaskHistory` |
| Peer-to-peer synchronization | Implemented for block broadcast with channel preservation. | `P2PNetworkService.BroadcastBlockAsync` |
| Peer management hardening | Implemented: `AddPeer` validates the supplied admin token against the node `NodeAdminToken` using constant-time comparison, and the browser UI no longer stores or submits this token. | `BlockchainGrpcService.AddPeer`, `NodeAdminToken` |
| Secure chain adoption | Implemented: longer peer chains are accepted only after structural validation and full contract replay on temporary state. | `BlockchainManager.TryAdoptChain`, replay validation helpers |
| Encrypted local storage | Implemented as application-level field encryption for block payloads, validator keys, signatures, task fields, roles, documents, and mempool payloads when `NodeDbPassword` is configured. | `DatabaseManager.EncryptString`, node configuration |
| Role-based team management | Implemented through project member state and RBAC checks. | `ProjectMembers`, access-control contract |
| Identity binding for team members | Implemented: role assignment includes target public key binding, mutation contracts reject spoofed known users, and project-scoped reads require signed requests from the bound key. | `AssignRole` payload with `TargetPublicKey`, `BlockchainGrpcService.IsAuthorizedReadRequest` |
| Governance / collective decisions | Implemented as proposal creation and yes/no voting with quorum-based acceptance or rejection. | `GovernanceProposals`, `GovernanceVotes`, `GovernanceTab` |
| Document versioning | Implemented as project-scoped document events with immutable version history and content hashes. | `DocumentVersions`, `DocumentsTab`, `GetProjectDocuments`, `GetDocumentVersions` |
| Git / artifact integration | Implemented with verified webhook intake, server-side blockchain anchoring, configurable IPFS API/gateway URLs, and repository/artifact views. | `Program.cs` webhook endpoint, `OracleIdentity`, `IpfsService`, repository/artifact UI |
| Analytics | Implemented as project-scoped dashboard analytics instead of global sample data, including document count, block throughput metrics, and commit/artifact counts recovered from the selected chain. | `AnalyticsTab`, `GetAnalytics` |
| Automated security audit | Implemented as a node-side audit endpoint that checks block hashes, signatures, proof-of-work, and chain links. | `GetSecurityAudit`, `SecurityAuditResponse` |
| Automated verification | Expanded with tests for channel consistency, unauthorized governance voting, quorum acceptance, document history, identity spoofing rejection, replay guard behavior, secure chain-adoption rejection paths, index rebuild, encryption, and prototype-scale chain reads. | `Blockchain.Tests`, `Blockchain.UI.Tests` |

## Recently Completed Gaps

### Project-Scoped History

Task history now requires a project id and user name. The node validates project membership before returning task history and reads only the selected project's blockchain channel.

### Governance Workflow

The application now supports a complete governance path:

1. A project member creates a proposal.
2. Project members vote yes or no.
3. The system counts project members.
4. A proposal is accepted or rejected when the quorum threshold is reached.
5. The dashboard shows proposal status, vote counts, and the current user's vote.

### Document Versioning

Project documents are now stored as signed blockchain events. The current document list is served from the `DocumentVersions` state index, while each version remains recoverable from project-channel blocks. The UI exposes a Documents tab where users can create a document, save a new version, and inspect version history with content hashes.

### Automated Audit And Metrics

The node exposes `GetSecurityAudit` for section 4 evidence. It checks the selected project chain for hash mismatches, invalid ECDSA signatures, proof-of-work failures, and broken previous-hash links. Analytics now include document count, average block interval, blocks per minute, and total security findings.

### Channel Consistency Fix

Project chain reads and writes now use the same sanitized channel name. This prevents duplicate or conflicting block indexes when moving tasks inside a project board.

### Safer Contract Logging

Access-control debug logs no longer print the full serialized contract payload. They print a compact operation summary instead, which is safer for encrypted-storage demonstrations.

### Signed Read Authorization

Project-scoped read endpoints now verify `AuthSignature` on the node. The UI signs `READ:{scope}:{userName}:{publicKey}:{timestamp}:{nonce}`, and the node checks the signature, timestamp TTL, nonce replay status, and bound public key before serving chains, tasks, task history, governance proposals, documents, analytics, security audit data, or the user's project list.

### Peer Admin Authorization

Peer registration now rejects missing or invalid `NodeAdminToken` values. The token is compared with `CryptographicOperations.FixedTimeEquals`, and the browser UI no longer reads the token from `wwwroot` configuration. Peer administration is expected to use node bootstrap configuration or another server-side admin path.

### Recoverable Analytics

Commit and artifact totals are now derived from the selected project blockchain channel instead of process-local static counters. After a node restart, analytics can be rebuilt from stored blocks.

### Configurable IPFS Endpoints

The UI reads `IpfsApiUrl` for uploads and `IpfsGatewayUrl` for artifact/diff reads from app settings. Local development still defaults to `http://127.0.0.1:5001/api/v0/add` and `http://127.0.0.1:8080/ipfs`, but those values are now configurable for the upload service, artifacts tab, and repository page.

## Defense Demo Scenario

Use this sequence to demonstrate the system:

1. Start the blockchain node and UI.
2. Configure or unlock the local database password.
3. Create a project.
4. Create a task inside the project.
5. Move the task through the board columns.
6. Open task history and show that the task events are project-scoped.
7. Open the governance tab.
8. Create a proposal.
9. Vote on the proposal from project member accounts.
10. Show quorum-based status change.
11. Open analytics and show that metrics are based only on the selected project.
12. Show the automated tests covering security and blockchain consistency.
13. Open the Documents tab, create a document, update it, and show version history.
14. Open Analytics and show the automated security audit status and block throughput metrics.

## Verification Commands

```powershell
dotnet build Blockchain.Node\Blockchain.Node.csproj --no-dependencies
dotnet build Blockchain.UI\Blockchain.UI.csproj
dotnet test Blockchain.Tests\Blockchain.Tests.csproj
dotnet test Blockchain.UI.Tests\Blockchain.UI.Tests.csproj
```

Latest local verification:

- `dotnet build Blockchain.Node\Blockchain.Node.csproj` passed.
- `dotnet build Blockchain.UI\Blockchain.UI.csproj` passed.
- `dotnet test Blockchain.Tests\Blockchain.Tests.csproj` passed: 25 tests.
- `dotnet test Blockchain.UI.Tests\Blockchain.UI.Tests.csproj` passed: 3 tests.

## Remaining Non-Code Thesis Work

The core implementation now covers the diploma plan's functional requirements. The remaining work is mainly thesis evidence and presentation material:

| Thesis artifact | What to prepare |
| --- | --- |
| Architecture section | Include diagrams for UI, node, blockchain core, database, and P2P synchronization. |
| Security section | Explain field-level encrypted storage, signed read authorization, smart-contract role checks, project isolation, admin-token peer management, and reduced sensitive logging. |
| Testing section | Include the current automated test results and describe manual demo cases. |
| Evaluation section | Add screenshots from the dashboard, documents workflow, governance workflow, analytics, and security audit. |
| Limitations section | Mention that the prototype uses simplified quorum governance and local node configuration. |
