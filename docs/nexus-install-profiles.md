# Nexus install profiles

Nexus supports three practical node profiles for product installs:

- `local-node`: private single-machine node with UI, Immediate finality, no Raft, no Iroh.
- `edge-node`: user workstation node with UI and Iroh sidecar, joins an existing network without becoming a Raft voter.
- `bootstrap-node` / `consensus-node`: stable network core nodes with PoC over Raft finality.

## Local private node

Generate config:

```powershell
dotnet run --project Nexus.Setup -- setup local
```

Start:

```powershell
dotnet run --project Nexus.Setup -- validate --env deploy/local-node.env
docker compose --env-file deploy/local-node.env -f deploy/docker-compose.local-node.yml up -d --build
```

Check:

```powershell
curl.exe -fsS http://localhost:7040/healthz
curl.exe -fsS http://localhost:7040/api/network/status
```

UI: `http://localhost:7080`.

## Create a network

Generate bootstrap config:

```powershell
dotnet run --project Nexus.Setup -- setup bootstrap --public-url https://bootstrap.example.com --grpc-url https://bootstrap.example.com:7141 --raft-endpoint bootstrap.example.com:6041
```

If `RAFT_PEER_ENDPOINT` is left empty, the bootstrap node starts as a single-member Raft cluster. This is valid for creating a network and using the app, but it has no consensus fault tolerance until another consensus node is added.

Start:

```powershell
dotnet run --project Nexus.Setup -- validate --env deploy/bootstrap-node.env
docker compose --env-file deploy/bootstrap-node.env -f deploy/docker-compose.bootstrap.yml up -d --build
```

Get invite:

```powershell
dotnet run --project Nexus.Setup -- invite --url https://bootstrap.example.com
```

Use the `token` field as the connection invite for edge nodes.

## Join a network as Edge

Generate config from an invite:

```powershell
dotnet run --project Nexus.Setup -- setup edge --invite <invite-token>
```

Start:

```powershell
dotnet run --project Nexus.Setup -- validate --env deploy/edge-node.env
docker compose --env-file deploy/edge-node.env -f deploy/docker-compose.edge-node.yml up -d --build
```

Check:

```powershell
curl.exe -fsS http://localhost:7042/healthz
curl.exe -fsS http://localhost:7042/api/network/status
dotnet run --project Nexus.Setup -- check --url http://localhost:7042
```

UI: `http://localhost:7082`.

In the Network tab, paste `NODE_ADMIN_TOKEN` into the admin token field to approve/revoke peers or trigger `Sync now`.

## Release smoke

After bootstrap and edge are running, verify the product path:

```powershell
dotnet run --project tools/RaftGrpcSmoke -- create-project --submit-url http://localhost:7042 --verify-urls http://localhost:7041,http://localhost:7042 --project-id SmokeProjectViaEdge --user SmokeUserEdge
```

Expected result: `accepted=true`, `Message="Raft majority commit reached"`, and both bootstrap and edge show the new System block hash in `/api/network/status`.

## Promote to consensus

Consensus nodes can run with either `RAFT_TRANSPORT=Tcp` or `RAFT_TRANSPORT=Iroh`. TCP remains the default for stable public servers. Iroh requires a running sidecar, a persistent Iroh identity, and approved consensus peer mappings such as `RAFT_IROH_PEERS=node-a=iroh://...,node-b=iroh://...`. Machines that are not approved consensus members should run as Edge nodes and use Iroh forwarding instead of joining the Raft majority. Generate config:

```powershell
dotnet run --project Nexus.Setup -- setup consensus --invite <invite-token>
```

For TCP, set the real `RAFT_PUBLIC_ENDPOINT`, `RAFT_PEER_ID`, and `RAFT_PEER_ENDPOINT` before starting. For Iroh, set `RAFT_TRANSPORT=Iroh`, `RAFT_IROH_NODE_ID`, `RAFT_IROH_CONTROL_ENDPOINT`, `RAFT_IROH_NODE_LISTEN_ENDPOINT`, and `RAFT_IROH_PEERS`.

```powershell
dotnet run --project Nexus.Setup -- validate --env deploy/consensus-node.env
docker compose --env-file deploy/consensus-node.env -f deploy/docker-compose.full-node.yml up -d --build
```

After the node registers with bootstrap, open the Network tab on the bootstrap node, paste `NODE_ADMIN_TOKEN`, approve the peer, and set its registry role to `Consensus`. Use `Edge` to demote a workstation node back out of the consensus core.

## Diagnostics

Use these endpoints on any node:

```powershell
curl.exe -fsS http://localhost:<node-port>/api/setup/diagnostics
curl.exe -fsS http://localhost:<node-port>/api/network/status
curl.exe -fsS http://localhost:<node-port>/api/consensus/raft/status
```

Or use the CLI summary:

```powershell
dotnet run --project Nexus.Setup -- check --url http://localhost:<node-port>
```

Expected product behavior:

- Edge nodes do not require open Raft ports.
- `RAFT_TRANSPORT=Iroh` should show Iroh Raft peer diagnostics and must not require a public Raft TCP endpoint.
- Edge nodes should show Iroh enabled and a bootstrap peer configured.
- Edge proposal forwarding tries approved Iroh peers first, then falls back to outgoing gRPC `ReceiveBlock` calls to configured bootstrap/consensus peers.
- Bootstrap/Consensus nodes using Raft must show valid Raft node id and public endpoint.
- Bootstrap nodes may start with no remote Raft peers; diagnostics should show a single-member warning, not an error.
- `CONSENSUS_REQUIRE_PROOF_OF_WORK` should be `false` for product network profiles.
