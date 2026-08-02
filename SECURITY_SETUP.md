# Security Setup

`Blockchain.Node` requires secrets from user-secrets or environment variables.
Do not store real values in `appsettings.json`.

## Local Development

Initialize and set local secrets:

```powershell
dotnet user-secrets init --project Blockchain.Node
dotnet user-secrets set "NodeDbPassword" "REPLACE_WITH_LONG_RANDOM_PASSWORD" --project Blockchain.Node
dotnet user-secrets set "NodeAdminToken" "REPLACE_WITH_LONG_RANDOM_ADMIN_TOKEN" --project Blockchain.Node
dotnet user-secrets set "OraclePrivateKeyPassword" "REPLACE_WITH_LONG_RANDOM_PASSWORD" --project Blockchain.Node
dotnet user-secrets set "WebhookSecret" "REPLACE_WITH_LONG_RANDOM_SECRET" --project Blockchain.Node
dotnet user-secrets set "OraclePublicKey" "REPLACE_WITH_ORACLE_PUBLIC_KEY" --project Blockchain.Node
```

## Environment Variables

For non-development launches, set:

```powershell
$env:NodeDbPassword="REPLACE_WITH_LONG_RANDOM_PASSWORD"
$env:NodeAdminToken="REPLACE_WITH_LONG_RANDOM_ADMIN_TOKEN"
$env:OraclePrivateKeyPassword="REPLACE_WITH_LONG_RANDOM_PASSWORD"
$env:WebhookSecret="REPLACE_WITH_LONG_RANDOM_SECRET"
$env:OraclePublicKey="REPLACE_WITH_ORACLE_PUBLIC_KEY"
dotnet run --project Blockchain.Node
```

`OraclePublicKey` must match the public key derived from the local oracle private key.
If they mismatch, the node startup will fail by design.

## Database Key Rotation

The previous local database key was stored in configuration, so the local database
can be rotated by deleting `Blockchain.Node/nexus_node_5041.db`. On the next node
startup, the application creates a fresh SQLite database and uses `NodeDbPassword`
for field-level encryption of stored payloads, keys, signatures, task metadata,
roles, documents, and mempool data.

## Local Raft Nodes

Run nodes on different ports. If `ConnectionStrings:DefaultNodeDb` is left as
`nexus_node_5041.db`, the application automatically derives the database name
from the port:

```powershell
dotnet run --project Blockchain.Node --no-launch-profile --urls http://localhost:5041
dotnet run --project Blockchain.Node --no-launch-profile --urls http://localhost:5042
```

This creates separate encrypted databases:

```text
Blockchain.Node/nexus_node_5041.db
Blockchain.Node/nexus_node_5042.db
```

Use the maintained local Raft launcher instead of starting unrelated P2P nodes:

```powershell
deploy\Start-LocalRaftPair.ps1
```

Do not put `NodeAdminToken` in `Blockchain.UI/wwwroot` configuration. Browser
configuration is public. Peer registration should be handled through node
bootstrap configuration, environment-specific server tooling, or another
server-side admin path that keeps the token off the client.

## Passkey Wallet Notes

Passkey-protected wallet export/import now uses WebAuthn large-blob storage on the
platform authenticator. Use a browser/authenticator combination that supports
the `largeBlob` extension.

## Migration For Legacy Members Without Bound Public Keys

Project-scoped read/write access now requires key binding for each member.
If a user existed before this change and has no key in `Users`, the project
owner must re-assign the member role with `TargetPublicKey` from the current UI.

Recommended sequence:

1. Member logs in and sends their public key + fingerprint to the project owner.
2. Owner opens Team Management and grants role with that key.
3. Member refreshes session and retries project-scoped actions.

Peers are persisted in the encrypted SQLite database and reloaded on startup.
Consensus members apply only DotNext Raft commits. Edge nodes catch up only from
blocks carrying persisted Raft finality metadata.
