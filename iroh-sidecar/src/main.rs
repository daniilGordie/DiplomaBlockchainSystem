use std::{
    collections::VecDeque,
    fs,
    net::SocketAddr,
    path::PathBuf,
    str::FromStr,
    sync::Arc,
    time::Duration,
};

use anyhow::{anyhow, Context, Result};
use axum::{
    extract::{Query, State},
    http::StatusCode,
    routing::{get, post},
    Json, Router,
};
use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use clap::Parser;
use iroh::{endpoint::{presets, RelayMode}, Endpoint, EndpointAddr, EndpointId, SecretKey};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use tokio::sync::Mutex;
use tracing::{info, warn};

const ALPN: &[u8] = b"nexus-blockchain/iroh/1";

#[derive(Parser, Debug)]
struct Args {
    #[arg(long, env = "NEXUS_IROH_LISTEN", default_value = "127.0.0.1:49152")]
    listen: SocketAddr,

    #[arg(long, env = "NEXUS_NODE_URL", default_value = "http://127.0.0.1:5041")]
    node_url: String,

    #[arg(long, env = "NEXUS_IROH_TOKEN")]
    local_api_token: String,

    #[arg(long, env = "NEXUS_IROH_SECRET_KEY_PATH", default_value = "iroh-secret.key")]
    secret_key_path: PathBuf,

    #[arg(long, env = "NEXUS_IROH_RELAY_MODE", default_value = "default")]
    relay_mode: String,
}

#[derive(Clone)]
struct AppState {
    endpoint: Endpoint,
    node_url: String,
    local_api_token: String,
    inbound_blocks: Arc<Mutex<VecDeque<Value>>>,
    http: reqwest::Client,
}

#[derive(Debug, Deserialize)]
struct PeerRequest {
    peer: String,
}

#[derive(Debug, Deserialize)]
struct ChainRequest {
    peer: String,
    #[serde(rename = "channelId")]
    channel_id: String,
}

#[derive(Debug, Deserialize)]
struct SubmitBlockRequest {
    peer: String,
    block: Value,
}

#[derive(Debug, Deserialize)]
struct BroadcastRequest {
    peers: Vec<String>,
    block: Value,
}

#[derive(Debug, Deserialize)]
struct EventsQuery {
    limit: Option<usize>,
}

#[derive(Debug, Serialize, Deserialize)]
struct WireMessage {
    kind: String,
    payload: Value,
}

#[derive(Debug, Serialize, Deserialize)]
struct WireResponse {
    success: bool,
    message: String,
    payload: Value,
}

#[derive(Debug, Serialize)]
struct StatusResponse {
    #[serde(rename = "nodeId")]
    node_id: String,
    #[serde(rename = "publicUrl")]
    public_url: String,
    #[serde(rename = "directAddresses")]
    direct_addresses: Vec<String>,
    #[serde(rename = "relayUrl")]
    relay_url: String,
}

#[derive(Debug, Serialize)]
struct EventsResponse {
    blocks: Vec<Value>,
}

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(tracing_subscriber::EnvFilter::from_default_env())
        .init();

    let args = Args::parse();
    if args.local_api_token.trim().is_empty() {
        return Err(anyhow!("NEXUS_IROH_TOKEN/local-api-token is required"));
    }

    let secret_key = load_or_create_secret_key(&args.secret_key_path)?;
    let relay_mode = parse_relay_mode(&args.relay_mode)?;
    let endpoint = Endpoint::builder(presets::N0)
        .relay_mode(relay_mode.clone())
        .secret_key(secret_key)
        .alpns(vec![ALPN.to_vec()])
        .bind()
        .await
        .context("failed to bind iroh endpoint")?;

    let state = AppState {
        endpoint: endpoint.clone(),
        node_url: args.node_url.trim_end_matches('/').to_string(),
        local_api_token: args.local_api_token,
        inbound_blocks: Arc::new(Mutex::new(VecDeque::new())),
        http: reqwest::Client::builder()
            .timeout(Duration::from_secs(30))
            .build()
            .context("failed to build HTTP client")?,
    };

    let accept_endpoint = endpoint.clone();
    let handler_state = state.clone();
    tokio::spawn(async move {
        loop {
            let Some(connecting) = accept_endpoint.accept().await else {
                break;
            };

            let state = handler_state.clone();
            tokio::spawn(async move {
                match connecting.await {
                    Ok(connection) => {
                        if let Err(err) = handle_iroh_connection(state, connection).await {
                            warn!("iroh connection failed: {err:#}");
                        }
                    }
                    Err(err) => warn!("iroh handshake failed: {err:#}"),
                }
            });
        }
    });

    let app = Router::new()
        .route("/status", get(status))
        .route("/events", get(events))
        .route("/broadcast-block", post(broadcast_block))
        .route("/known-channels", post(known_channels))
        .route("/chain", post(chain))
        .route("/submit-block", post(submit_block))
        .with_state(state);

    info!("nexus iroh sidecar node_id={}", endpoint.id());
    info!("iroh relay mode={:?}", relay_mode);
    info!("local HTTP API listening on {}", args.listen);

    let listener = tokio::net::TcpListener::bind(args.listen)
        .await
        .context("failed to bind local HTTP API")?;
    axum::serve(listener, app).await?;
    Ok(())
}

async fn status(State(state): State<AppState>) -> Json<StatusResponse> {
    let endpoint_addr = state.endpoint.addr();
    let endpoint_id = state.endpoint.id().to_string();
    let encoded_addr = URL_SAFE_NO_PAD.encode(
        serde_json::to_vec(&endpoint_addr).expect("EndpointAddr must be serializable"),
    );
    let direct_addresses = endpoint_addr.ip_addrs().map(ToString::to_string).collect();
    let relay_url = endpoint_addr
        .relay_urls()
        .next()
        .map(ToString::to_string)
        .unwrap_or_default();

    Json(StatusResponse {
        public_url: format!("iroh://{endpoint_id}?addr={encoded_addr}"),
        node_id: endpoint_id,
        direct_addresses,
        relay_url,
    })
}

async fn events(
    State(state): State<AppState>,
    Query(query): Query<EventsQuery>,
) -> Json<EventsResponse> {
    let limit = query.limit.unwrap_or(100).clamp(1, 500);
    let mut queue = state.inbound_blocks.lock().await;
    let mut blocks = Vec::new();
    for _ in 0..limit {
        match queue.pop_front() {
            Some(block) => blocks.push(block),
            None => break,
        }
    }

    Json(EventsResponse { blocks })
}

async fn broadcast_block(
    State(state): State<AppState>,
    Json(request): Json<BroadcastRequest>,
) -> Result<Json<Value>, (StatusCode, String)> {
    let message = WireMessage {
        kind: "BroadcastBlock".to_string(),
        payload: request.block,
    };

    let mut accepted = 0usize;
    let mut failures = Vec::new();
    for peer in request.peers {
        match send_wire_message(&state, &peer, &message).await {
            Ok(response) if response.success => accepted += 1,
            Ok(response) => failures.push(format!("{peer}: {}", response.message)),
            Err(err) => failures.push(format!("{peer}: {err:#}")),
        }
    }

    Ok(Json(json!({
        "accepted": accepted,
        "failures": failures,
    })))
}

async fn known_channels(
    State(state): State<AppState>,
    Json(request): Json<PeerRequest>,
) -> Result<Json<Value>, (StatusCode, String)> {
    let response = send_wire_message(
        &state,
        &request.peer,
        &WireMessage {
            kind: "KnownChannelsRequest".to_string(),
            payload: json!({}),
        },
    )
    .await
    .map_err(internal_error)?;

    if response.success {
        Ok(Json(response.payload))
    } else {
        Err((StatusCode::BAD_GATEWAY, response.message))
    }
}

async fn chain(
    State(state): State<AppState>,
    Json(request): Json<ChainRequest>,
) -> Result<Json<Value>, (StatusCode, String)> {
    let response = send_wire_message(
        &state,
        &request.peer,
        &WireMessage {
            kind: "ChainRequest".to_string(),
            payload: json!({ "channelId": request.channel_id }),
        },
    )
    .await
    .map_err(internal_error)?;

    if response.success {
        Ok(Json(response.payload))
    } else {
        Err((StatusCode::BAD_GATEWAY, response.message))
    }
}

async fn submit_block(
    State(state): State<AppState>,
    Json(request): Json<SubmitBlockRequest>,
) -> Result<Json<Value>, (StatusCode, String)> {
    let response = send_wire_message(
        &state,
        &request.peer,
        &WireMessage {
            kind: "SubmitBlock".to_string(),
            payload: request.block,
        },
    )
    .await
    .map_err(internal_error)?;

    if response.success {
        Ok(Json(response.payload))
    } else {
        Err((StatusCode::BAD_GATEWAY, response.message))
    }
}

async fn handle_iroh_connection(state: AppState, connection: iroh::endpoint::Connection) -> Result<()> {
    let (mut send, mut recv) = connection.accept_bi().await?;
    let bytes = recv.read_to_end(16 * 1024 * 1024).await?;
    let message: WireMessage = serde_json::from_slice(&bytes)?;
    let response = handle_wire_message(state, message).await;
    send.write_all(&serde_json::to_vec(&response)?).await?;
    send.finish()?;
    send.stopped().await?;
    Ok(())
}

async fn handle_wire_message(state: AppState, message: WireMessage) -> WireResponse {
    match message.kind.as_str() {
        "BroadcastBlock" => {
            state.inbound_blocks.lock().await.push_back(message.payload);
            WireResponse {
                success: true,
                message: "accepted".to_string(),
                payload: json!({}),
            }
        }
        "KnownChannelsRequest" => match local_get(&state, "/api/p2p/iroh/known-channels").await {
            Ok(payload) => WireResponse {
                success: true,
                message: "ok".to_string(),
                payload,
            },
            Err(err) => WireResponse {
                success: false,
                message: format!("{err:#}"),
                payload: json!({}),
            },
        },
        "ChainRequest" => {
            let channel_id = message
                .payload
                .get("channelId")
                .and_then(Value::as_str)
                .unwrap_or("System");
            match local_get(
                &state,
                &format!("/api/p2p/iroh/chain/{}", urlencoding::encode(channel_id)),
            )
            .await
            {
                Ok(payload) => WireResponse {
                    success: true,
                    message: "ok".to_string(),
                    payload,
                },
                Err(err) => WireResponse {
                    success: false,
                    message: format!("{err:#}"),
                    payload: json!({}),
                },
            }
        }
        "SubmitBlock" => match local_post(&state, "/api/p2p/iroh/submit-block", &message.payload).await {
            Ok(payload) => WireResponse {
                success: payload
                    .get("success")
                    .and_then(Value::as_bool)
                    .unwrap_or(false),
                message: payload
                    .get("message")
                    .and_then(Value::as_str)
                    .unwrap_or("submit-block completed")
                    .to_string(),
                payload,
            },
            Err(err) => WireResponse {
                success: false,
                message: format!("{err:#}"),
                payload: json!({}),
            },
        },
        other => WireResponse {
            success: false,
            message: format!("unsupported message kind: {other}"),
            payload: json!({}),
        },
    }
}

async fn send_wire_message(state: &AppState, peer: &str, message: &WireMessage) -> Result<WireResponse> {
    let peer_addr = parse_peer_addr(peer)?;
    let connection = state
        .endpoint
        .connect(peer_addr, ALPN)
        .await
        .context("failed to connect to iroh peer")?;
    let (mut send, mut recv) = connection.open_bi().await?;
    send.write_all(&serde_json::to_vec(message)?).await?;
    send.finish()?;
    send.stopped().await?;
    let bytes = recv.read_to_end(16 * 1024 * 1024).await?;
    Ok(serde_json::from_slice(&bytes)?)
}

async fn local_get(state: &AppState, path: &str) -> Result<Value> {
    let response = state
        .http
        .get(format!("{}{}", state.node_url, path))
        .header("X-Nexus-Iroh-Token", &state.local_api_token)
        .send()
        .await
        .context("local node request failed")?;

    let status = response.status();
    let text = response.text().await.unwrap_or_default();
    if !status.is_success() {
        return Err(anyhow!("local node returned HTTP {status}: {text}"));
    }

    Ok(serde_json::from_str(&text)?)
}

async fn local_post(state: &AppState, path: &str, payload: &Value) -> Result<Value> {
    let response = state
        .http
        .post(format!("{}{}", state.node_url, path))
        .header("X-Nexus-Iroh-Token", &state.local_api_token)
        .json(payload)
        .send()
        .await
        .context("local node request failed")?;

    let status = response.status();
    let text = response.text().await.unwrap_or_default();
    if !status.is_success() {
        return Err(anyhow!("local node returned HTTP {status}: {text}"));
    }

    Ok(serde_json::from_str(&text)?)
}

fn internal_error(err: anyhow::Error) -> (StatusCode, String) {
    let message = format!("{err:#}");
    warn!("HTTP proxy operation failed: {message}");
    (StatusCode::BAD_GATEWAY, message)
}

fn parse_peer_addr(peer: &str) -> Result<EndpointAddr> {
    let value = peer.strip_prefix("iroh://").unwrap_or(peer);
    let endpoint_id_text = value.split_once('?').map(|(id, _)| id).unwrap_or(value);
    let peer_id = EndpointId::from_str(endpoint_id_text).context("invalid iroh peer id")?;
    Ok(EndpointAddr::from(peer_id))
}

fn parse_relay_mode(value: &str) -> Result<RelayMode> {
    match value.trim().to_ascii_lowercase().as_str() {
        "" | "default" | "production" | "prod" => Ok(RelayMode::Default),
        "staging" | "stage" => Ok(RelayMode::Staging),
        "disabled" | "disable" | "off" | "none" => Ok(RelayMode::Disabled),
        other => Err(anyhow!("unsupported NEXUS_IROH_RELAY_MODE: {other}; use default, staging, or disabled")),
    }
}

fn load_or_create_secret_key(path: &PathBuf) -> Result<SecretKey> {
    if path.exists() {
        let text = fs::read_to_string(path).context("failed to read Iroh secret key file")?;
        let bytes = hex::decode(text.trim()).context("failed to decode Iroh secret key")?;
        let bytes: [u8; 32] = bytes
            .try_into()
            .map_err(|_| anyhow!("Iroh secret key must contain exactly 32 bytes"))?;
        return Ok(SecretKey::from_bytes(&bytes));
    }

    if let Some(parent) = path.parent() {
        if !parent.as_os_str().is_empty() {
            fs::create_dir_all(parent).context("failed to create Iroh identity directory")?;
        }
    }

    let secret_key = SecretKey::generate();
    fs::write(path, hex::encode(secret_key.to_bytes())).context("failed to write Iroh secret key file")?;
    Ok(secret_key)
}
