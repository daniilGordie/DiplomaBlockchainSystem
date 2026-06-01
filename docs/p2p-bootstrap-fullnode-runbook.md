# P2P Bootstrap/Full-Node Runbook

## Roles

- `Bootstrap` node is the public peer directory. It accepts `RegisterPeer`, stores known nodes, and returns `GetPeerDirectory`.
- `Full` node stores blockchain data, serves UI gRPC/SignalR traffic, registers itself with bootstrap nodes, discovers other full nodes, and syncs chains directly.

## Required Configuration

```json
"P2P": {
  "NodeRole": "Full",
  "NodeId": "full-node-a",
  "PublicUrl": "https://node-a.example.com",
  "BootstrapPeers": [ "https://bootstrap.example.com" ],
  "SyncToken": "shared-sync-token",
  "RegistrationToken": "shared-registration-token"
}
```

`SyncToken` authorizes node-to-node chain reads. `RegistrationToken` authorizes full-node registration with bootstrap nodes. `NodeAdminToken` remains only for manual peer administration.

## Demo Topology

```text
bootstrap-node: http://localhost:7000
full-node-a:    http://localhost:7001
full-node-b:    http://localhost:7002
ui:             http://localhost:7003
```

Start:

```powershell
docker compose -f docker-compose.p2p-demo.yml up --build
```

Expected flow:

1. `full-node-a` and `full-node-b` call `RegisterPeer` on `bootstrap-node`.
2. Each full node calls `GetPeerDirectory` and discovers the other full node.
3. Each full node syncs known channels using paged `GetChain` requests.
4. New accepted blocks are broadcast to known peers and pushed to UI clients through SignalR.

For a real deployment, replace container-local `PublicUrl` values with externally reachable HTTPS URLs.

The demo compose uses `OraclePublicKey=auto` and a shared `OracleKeyPath` volume so all demo nodes trust the same generated oracle key. For production, configure an explicit `OraclePublicKey` and persist/provision the matching oracle private key.
