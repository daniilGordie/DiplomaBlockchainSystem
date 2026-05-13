# Git and IPFS Integration Runbook

This project uses a one-way trusted oracle flow for source-code anchoring:

`git commit or provider push event -> signed Node webhook -> normalized GitEvent -> oracle-signed CodeCommit block -> P2P broadcast -> UI repository and audit trail`

The UI does not sign Git commits automatically. The Node webhook validates the HMAC signature, rejects replayed commits, normalizes provider-specific payloads into a `Source = GitEvent` / `Type = CodeCommit` payload, signs it with the configured oracle identity, and anchors it into the project blockchain channel.

GitHub is treated as an event provider only. Blockchain contracts validate the normalized `GitEvent` shape and oracle signature, not a hard dependency on GitHub.

## Prerequisites

1. Configure `WebhookSecret`, `OraclePublicKey`, `OraclePrivateKeyPassword`, `NodeDbPassword`, and `NodeAdminToken` for `Blockchain.Node`.
2. Run a local IPFS daemon if artifact uploads must be verified:

```powershell
ipfs daemon
```

3. Make sure the local IPFS API is available at:

```text
http://127.0.0.1:5001/api/v0
```

## Start Two Nodes

Terminal 1:

```powershell
dotnet run --project Blockchain.Node --no-launch-profile --urls "https://localhost:7041;http://localhost:5041" --P2P:NodeId node-7041 --P2P:PublicUrl https://localhost:7041 --P2P:BootstrapPeers:0 https://localhost:7042
```

Terminal 2:

```powershell
dotnet run --project Blockchain.Node --no-launch-profile --urls "https://localhost:7042;http://localhost:5042" --P2P:NodeId node-7042 --P2P:PublicUrl https://localhost:7042 --P2P:BootstrapPeers:0 https://localhost:7041
```

## Install Git Hook

Run this from the repository root. Use an existing project id from the UI.

```powershell
.\install-githook.ps1 -NodeWebhookUrl "https://localhost:7041/api/webhooks/git" -ProjectId "YourProjectId" -WebhookSecret "dev-webhook-secret-123"
```

The script writes `.git/hooks/post-commit`. The hook runs `BlockChain.GitHook` after each local commit, computes a signed webhook request, and sends the commit metadata to the selected node.

## GitHub Push Provider

The same endpoint can receive GitHub `push` webhooks when they are signed with the configured `WebhookSecret`.

Use:
- Payload URL: `https://<node-public-url>/api/webhooks/git?projectId=<ProjectId>`
- Content type: `application/json`
- Secret: same value as `Blockchain.Node:WebhookSecret`
- Event: `push`

Node reads `repository.full_name`, `head_commit.id`, `head_commit.message`, and the commit author, then stores the blockchain payload as `Source = GitEvent` and `Provider = GitHubPush`.

Repository binding policy:
- Node enforces `1 repository <-> 1 project`.
- First valid commit creates the binding automatically.
- If the same repository sends another project id later, Node rejects the webhook.
- If one project id is already bound to another repository, Node rejects the webhook.

## Verify

1. Create or select a project in the UI.
2. Install the Git hook with the same project id.
3. Make a local commit.
4. Open the UI for `https://localhost:7041` and `https://localhost:7042`.
5. Check the repository/audit views for a `CodeCommit` record with:
   - repository name;
   - commit hash;
   - author;
   - block hash;
   - channel id;
   - verification status.
6. Check `GET /api/integrations/git/status` for `bindings` and `defaultProjectId` to confirm mapping.

## Health Endpoints

Git integration status:

```text
GET https://localhost:7041/api/integrations/git/status
```

IPFS integration health:

```text
GET https://localhost:7041/api/integrations/ipfs/health
```

If IPFS is not running, artifact uploads and the IPFS health card will report the node as unavailable.
