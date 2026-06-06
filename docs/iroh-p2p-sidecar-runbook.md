# Iroh P2P Sidecar Runbook

This mode keeps the blockchain application in .NET and moves NAT-friendly peer
transport into a Rust/Iroh sidecar.

## Architecture

`Blockchain.Node` still owns validation, persistence, SignalR notifications,
project authorization, and chain adoption.

`nexus-iroh-sidecar` owns network reachability:

- creates a stable Iroh endpoint identity from `NEXUS_IROH_SECRET_KEY_PATH`;
- exposes a local HTTP API for the .NET node;
- accepts Iroh QUIC connections using ALPN `nexus-blockchain/iroh/1`;
- receives block broadcasts and queues them for the .NET node to process;
- serves remote chain sync requests by calling the local .NET node.

Peer records use:

```text
iroh://<iroh-endpoint-id>?addr=<encoded-endpoint-address>
```

The bootstrap node can store these records even when it does not run an Iroh
sidecar itself.

## Required Node Configuration

Enable Iroh on each full node:

```text
P2P__Iroh__Enabled=true
P2P__Iroh__SidecarUrl=http://127.0.0.1:49152
P2P__Iroh__LocalApiToken=<strong local token>
P2P__PublicUrl=
P2P__BootstrapPeers__0=https://bootstrap.example.com
P2P__IdentityKeyPath=./data/node-identity.p256.key
P2P__AllowRegistrationTokenFallback=false
```

Run the sidecar next to that node:

```text
NEXUS_IROH_LISTEN=127.0.0.1:49152
NEXUS_NODE_URL=http://127.0.0.1:5041
NEXUS_IROH_TOKEN=<same strong local token>
NEXUS_IROH_SECRET_KEY_PATH=./data/iroh-secret.key
NEXUS_IROH_RELAY_MODE=default
```

`NEXUS_IROH_SECRET_KEY_PATH` must be persisted. If it is deleted, the node gets a
new Iroh identity and bootstrap peers will see it as a different node.

`P2P__IdentityKeyPath` must also be persisted. It is the .NET node identity used
to sign bootstrap registrations. If it is deleted, the bootstrap node will see a
new node public key even if the Iroh endpoint stayed the same.

## Production Compose Packages

Run a public bootstrap node:

```powershell
Copy-Item deploy/bootstrap-node.env.example deploy/bootstrap-node.env
notepad deploy/bootstrap-node.env
docker compose --env-file deploy/bootstrap-node.env -f deploy/docker-compose.bootstrap.yml up -d --build
```

Run a full node with an Iroh sidecar on another machine:

```powershell
Copy-Item deploy/full-node.env.example deploy/full-node.env
notepad deploy/full-node.env
docker compose --env-file deploy/full-node.env -f deploy/docker-compose.full-node.yml up -d --build
```

The full-node compose file persists:

- the SQLite node database;
- the .NET node signing identity;
- the Iroh endpoint secret key.

## Demo

```powershell
docker compose -f docker-compose.iroh-demo.yml up --build
```

The demo starts:

- one HTTP bootstrap node on `http://localhost:7041`;
- two full nodes on `http://localhost:7042` and `http://localhost:7043`;
- one Iroh sidecar for each full node.

The full nodes register `iroh://...` peer records with the bootstrap node,
discover each other, sync chains through Iroh, and broadcast accepted blocks
through Iroh.

## Security Notes

`P2P:Iroh:LocalApiToken` protects the local node endpoints used by the sidecar.
Do not expose the sidecar HTTP API publicly. It is intended to be reachable only
by the local node or the private container network.

The Iroh endpoint id is derived from the sidecar secret key. Iroh authenticates
the QUIC connection to that endpoint id.

Bootstrap registration is signed with the .NET node identity:

```text
NEXUS_NODE_REGISTRATION_V1
<node-id>
<public-url>
<role>
<timestamp>
<nonce>
```

The bootstrap node verifies the ECDSA signature, timestamp skew, nonce replay,
and a per-remote-address registration rate limit before saving the peer record.
`P2P:RegistrationToken` fallback is disabled by default and should stay disabled
for public networks.

Set `NEXUS_IROH_RELAY_MODE` explicitly:

- `default`: Iroh production relay defaults;
- `staging`: staging relays for test networks;
- `disabled`: direct-address mode only.

The default Iroh relay configuration is suitable for development and small
pilots. A public production network should document and pin its relay policy in
the deployment environment, then move to a dedicated relay fleet when operating
at public scale.
