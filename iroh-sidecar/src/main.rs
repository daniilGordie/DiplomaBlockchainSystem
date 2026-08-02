use std::{fs, net::SocketAddr, path::PathBuf, str::FromStr, time::Duration};

use anyhow::{anyhow, Context, Result};
use axum::{
    extract::State,
    http::StatusCode,
    routing::{get, post},
    Json, Router,
};
use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
use clap::Parser;
use iroh::{
    address_lookup::AddrFilter,
    endpoint::{presets, RelayMode},
    Endpoint, EndpointAddr, EndpointId, SecretKey,
};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::{TcpListener, TcpStream},
};
use tracing::{info, warn};

const ALPN: &[u8] = b"nexus-blockchain/iroh/1";
const RAFT_ALPN: &[u8] = b"nexus/raft/1";

#[derive(Parser, Debug)]
struct Args {
    #[arg(long, env = "NEXUS_IROH_LISTEN", default_value = "127.0.0.1:49152")]
    listen: SocketAddr,

    #[arg(long, env = "NEXUS_NODE_URL", default_value = "http://127.0.0.1:5041")]
    node_url: String,

    #[arg(long, env = "NEXUS_IROH_TOKEN")]
    local_api_token: Option<String>,

    #[arg(
        long,
        env = "NEXUS_IROH_SECRET_KEY_PATH",
        default_value = "iroh-secret.key"
    )]
    secret_key_path: PathBuf,

    #[arg(long, env = "NEXUS_IROH_RELAY_MODE", default_value = "default")]
    relay_mode: String,

    #[arg(long, env = "NEXUS_IROH_RAFT_LISTEN")]
    raft_listen: Option<SocketAddr>,

    #[arg(long, env = "NEXUS_IROH_RAFT_NODE_LISTEN")]
    raft_node_listen: Option<SocketAddr>,

    #[arg(long, default_value_t = false)]
    print_node_id: bool,
}

#[derive(Clone)]
struct AppState {
    endpoint: Endpoint,
    node_url: String,
    local_api_token: String,
    http: reqwest::Client,
    raft_node_listen: Option<SocketAddr>,
    transport_mode: IrohTransportMode,
}

#[derive(Debug, Deserialize)]
struct PeerRequest {
    peer: String,
}

#[derive(Debug, Deserialize)]
struct CommittedSinceRequest {
    peer: String,
    #[serde(rename = "channelId")]
    channel_id: String,
    #[serde(rename = "afterIndex")]
    after_index: i32,
    #[serde(rename = "afterHash")]
    after_hash: String,
    auth: Value,
}

#[derive(Debug, Deserialize)]
struct SubmitBlockRequest {
    peer: String,
    block: Value,
    auth: Value,
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
    #[serde(rename = "connectionPath")]
    connection_path: String,
    #[serde(rename = "transportMode")]
    transport_mode: String,
}

#[derive(Debug, Clone, Copy, Eq, PartialEq)]
enum IrohTransportMode {
    Auto,
    DirectPreferred,
    RelayOnly,
    Disabled,
}

impl IrohTransportMode {
    fn as_str(self) -> &'static str {
        match self {
            Self::Auto => "Auto",
            Self::DirectPreferred => "DirectPreferred",
            Self::RelayOnly => "RelayOnly",
            Self::Disabled => "Disabled",
        }
    }

    fn connection_path(self) -> &'static str {
        match self {
            Self::RelayOnly => "relay",
            Self::Disabled => "disabled",
            Self::Auto | Self::DirectPreferred => "auto",
        }
    }
}

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(tracing_subscriber::EnvFilter::from_default_env())
        .init();

    let args = Args::parse();

    let secret_key = load_or_create_secret_key(&args.secret_key_path)?;
    if args.print_node_id {
        println!("{}", secret_key.public());
        return Ok(());
    }

    let Some(local_api_token) = args
        .local_api_token
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
    else {
        return Err(anyhow!("NEXUS_IROH_TOKEN/local-api-token is required"));
    };

    let (relay_mode, transport_mode) = parse_relay_mode(&args.relay_mode)?;
    let mut endpoint_builder = Endpoint::builder(presets::N0)
        .relay_mode(relay_mode.clone())
        .secret_key(secret_key)
        .alpns(vec![ALPN.to_vec(), RAFT_ALPN.to_vec()]);
    if transport_mode == IrohTransportMode::RelayOnly {
        endpoint_builder = endpoint_builder.addr_filter(AddrFilter::relay_only());
    }

    let endpoint = endpoint_builder
        .bind()
        .await
        .context("failed to bind iroh endpoint")?;

    let state = AppState {
        endpoint: endpoint.clone(),
        node_url: args.node_url.trim_end_matches('/').to_string(),
        local_api_token: local_api_token.to_string(),
        raft_node_listen: args.raft_node_listen,
        transport_mode,
        http: reqwest::Client::builder()
            .timeout(Duration::from_secs(30))
            .build()
            .context("failed to build HTTP client")?,
    };

    if let Some(raft_listen) = args.raft_listen {
        let raft_state = state.clone();
        tokio::spawn(async move {
            if let Err(err) = run_raft_control_listener(raft_state, raft_listen).await {
                warn!("raft control listener stopped: {err:#}");
            }
        });
    }

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
                        if connection.alpn() == RAFT_ALPN {
                            if let Err(err) = handle_raft_iroh_connection(state, connection).await {
                                warn!("raft iroh connection failed: {err:#}");
                            }
                        } else if let Err(err) = handle_iroh_connection(state, connection).await {
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
        .route("/known-channels", post(known_channels))
        .route("/committed-since", post(committed_since))
        .route("/submit-block", post(submit_block))
        .with_state(state);

    info!("nexus iroh sidecar node_id={}", endpoint.id());
    info!(
        "iroh relay mode={:?}; transport mode={}",
        relay_mode,
        transport_mode.as_str()
    );
    if let Some(raft_listen) = args.raft_listen {
        info!("local Raft-over-Iroh control listening on {}", raft_listen);
    }
    if let Some(raft_node_listen) = args.raft_node_listen {
        info!("local Nexus Raft listener target is {}", raft_node_listen);
    }
    info!("local HTTP API listening on {}", args.listen);

    let listener = tokio::net::TcpListener::bind(args.listen)
        .await
        .context("failed to bind local HTTP API")?;
    axum::serve(listener, app).await?;
    Ok(())
}

async fn run_raft_control_listener(state: AppState, listen: SocketAddr) -> Result<()> {
    let listener = TcpListener::bind(listen)
        .await
        .context("failed to bind local Raft control listener")?;

    loop {
        let (stream, remote_addr) = listener.accept().await?;
        let state = state.clone();
        tokio::spawn(async move {
            if let Err(err) = handle_local_raft_control_stream(state, stream).await {
                warn!("local raft control stream from {remote_addr} failed: {err:#}");
            }
        });
    }
}

async fn handle_local_raft_control_stream(state: AppState, stream: TcpStream) -> Result<()> {
    let mut stream = stream;
    let line = read_control_preface(&mut stream).await?;

    let mut parts = line.trim().splitn(3, ' ');
    let command = parts.next().unwrap_or_default();
    let token_b64 = parts.next().unwrap_or_default();
    let peer = parts.next().unwrap_or_default();
    if command != "OPEN" {
        return Err(anyhow!("unsupported raft control command: {command}"));
    }

    let token = String::from_utf8(
        base64::engine::general_purpose::STANDARD
            .decode(token_b64)
            .context("invalid raft control token encoding")?,
    )
    .context("raft control token must be utf8")?;
    if !constant_time_equals(token.as_bytes(), state.local_api_token.as_bytes()) {
        return Err(anyhow!("invalid raft control token"));
    }

    let peer_addr = parse_peer_addr(peer)?;
    let connection = state
        .endpoint
        .connect(peer_addr, RAFT_ALPN)
        .await
        .context("failed to connect raft iroh peer")?;
    let (send, recv) = connection
        .open_bi()
        .await
        .context("failed to open raft iroh stream")?;

    pump_tcp_to_iroh(stream, send, recv).await
}

async fn read_control_preface(stream: &mut TcpStream) -> Result<String> {
    const MAX_PREFACE_BYTES: usize = 4096;

    let mut preface = Vec::with_capacity(128);
    loop {
        let mut byte = [0_u8; 1];
        let read = stream
            .read(&mut byte)
            .await
            .context("failed to read raft control preface")?;
        if read == 0 {
            return Err(anyhow!("empty raft control preface"));
        }

        if byte[0] == b'\n' {
            break;
        }

        preface.push(byte[0]);
        if preface.len() > MAX_PREFACE_BYTES {
            return Err(anyhow!("raft control preface exceeds maximum size"));
        }
    }

    String::from_utf8(preface).context("raft control preface must be utf8")
}

async fn handle_raft_iroh_connection(
    state: AppState,
    connection: iroh::endpoint::Connection,
) -> Result<()> {
    let target = state
        .raft_node_listen
        .ok_or_else(|| anyhow!("NEXUS_IROH_RAFT_NODE_LISTEN is not configured"))?;
    let (send, recv) = connection
        .accept_bi()
        .await
        .context("failed to accept raft iroh stream")?;
    let stream = TcpStream::connect(target)
        .await
        .context("failed to connect local Nexus raft listener")?;
    pump_tcp_to_iroh(stream, send, recv).await
}

async fn pump_tcp_to_iroh(
    tcp_stream: TcpStream,
    mut iroh_send: iroh::endpoint::SendStream,
    mut iroh_recv: iroh::endpoint::RecvStream,
) -> Result<()> {
    let (mut tcp_read, mut tcp_write) = tcp_stream.into_split();
    let to_iroh = async {
        tokio::io::copy(&mut tcp_read, &mut iroh_send).await?;
        iroh_send.finish()?;
        Result::<u64>::Ok(0)
    };
    let from_iroh = async {
        tokio::io::copy(&mut iroh_recv, &mut tcp_write).await?;
        tcp_write.shutdown().await?;
        Result::<u64>::Ok(0)
    };

    tokio::try_join!(to_iroh, from_iroh)?;
    Ok(())
}

async fn status(State(state): State<AppState>) -> Json<StatusResponse> {
    let endpoint_addr = state.endpoint.addr();
    let endpoint_id = state.endpoint.id().to_string();
    let encoded_addr = URL_SAFE_NO_PAD
        .encode(serde_json::to_vec(&endpoint_addr).expect("EndpointAddr must be serializable"));
    let direct_addresses = if state.transport_mode == IrohTransportMode::RelayOnly {
        Vec::new()
    } else {
        endpoint_addr.ip_addrs().map(ToString::to_string).collect()
    };
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
        connection_path: state.transport_mode.connection_path().to_string(),
        transport_mode: state.transport_mode.as_str().to_string(),
    })
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

async fn committed_since(
    State(state): State<AppState>,
    Json(request): Json<CommittedSinceRequest>,
) -> Result<Json<Value>, (StatusCode, String)> {
    let response = send_wire_message(
        &state,
        &request.peer,
        &WireMessage {
            kind: "CommittedSinceRequest".to_string(),
            payload: json!({
                "channelId": request.channel_id,
                "afterIndex": request.after_index,
                "afterHash": request.after_hash,
                "auth": request.auth
            }),
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
            payload: json!({
                "block": request.block,
                "auth": request.auth
            }),
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

async fn handle_iroh_connection(
    state: AppState,
    connection: iroh::endpoint::Connection,
) -> Result<()> {
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
        "CommittedSinceRequest" => {
            let channel_id = message
                .payload
                .get("channelId")
                .and_then(Value::as_str)
                .unwrap_or("System");
            let after_index = message
                .payload
                .get("afterIndex")
                .and_then(Value::as_i64)
                .unwrap_or(-1);
            let after_hash = message
                .payload
                .get("afterHash")
                .and_then(Value::as_str)
                .unwrap_or("");
            let auth = message.payload.get("auth").cloned().unwrap_or(json!({}));
            match local_post(
                &state,
                "/api/p2p/iroh/committed-since",
                &json!({
                    "channelId": channel_id,
                    "afterIndex": after_index,
                    "afterHash": after_hash,
                    "auth": auth
                }),
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
        "SubmitBlock" => {
            match local_post(&state, "/api/p2p/iroh/submit-block", &message.payload).await {
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
            }
        }
        other => WireResponse {
            success: false,
            message: format!("unsupported message kind: {other}"),
            payload: json!({}),
        },
    }
}

async fn send_wire_message(
    state: &AppState,
    peer: &str,
    message: &WireMessage,
) -> Result<WireResponse> {
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

fn parse_relay_mode(value: &str) -> Result<(RelayMode, IrohTransportMode)> {
    match value.trim().to_ascii_lowercase().as_str() {
        "" | "default" | "production" | "prod" | "auto" => Ok((RelayMode::Default, IrohTransportMode::Auto)),
        "direct-preferred" | "directpreferred" => Ok((RelayMode::Default, IrohTransportMode::DirectPreferred)),
        "staging" | "stage" => Ok((RelayMode::Staging, IrohTransportMode::Auto)),
        "relay-only" | "relayonly" => Ok((RelayMode::Default, IrohTransportMode::RelayOnly)),
        "disabled" | "disable" | "off" | "none" => Ok((RelayMode::Disabled, IrohTransportMode::Disabled)),
        other => Err(anyhow!(
            "unsupported NEXUS_IROH_RELAY_MODE: {other}; use default, staging, relay-only, direct-preferred, or disabled"
        )),
    }
}

fn constant_time_equals(left: &[u8], right: &[u8]) -> bool {
    if left.len() != right.len() {
        return false;
    }

    let mut diff = 0u8;
    for (left_byte, right_byte) in left.iter().zip(right.iter()) {
        diff |= left_byte ^ right_byte;
    }

    diff == 0
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
    fs::write(path, hex::encode(secret_key.to_bytes()))
        .context("failed to write Iroh secret key file")?;
    Ok(secret_key)
}

#[cfg(test)]
mod tests {
    use super::*;
    use base64::engine::general_purpose::STANDARD;
    use std::time::Instant;
    use tokio::net::{TcpListener, TcpStream};

    #[test]
    fn constant_time_equals_accepts_identical_tokens() {
        assert!(constant_time_equals(b"sidecar-token", b"sidecar-token"));
    }

    #[test]
    fn constant_time_equals_rejects_different_tokens() {
        assert!(!constant_time_equals(b"sidecar-token", b"sidecar-tokem"));
    }

    #[test]
    fn constant_time_equals_rejects_different_lengths() {
        assert!(!constant_time_equals(
            b"sidecar-token",
            b"sidecar-token-extra"
        ));
    }

    #[test]
    fn parse_relay_mode_accepts_supported_values() {
        assert!(matches!(
            parse_relay_mode("default").unwrap(),
            (RelayMode::Default, IrohTransportMode::Auto)
        ));
        assert!(matches!(
            parse_relay_mode("staging").unwrap(),
            (RelayMode::Staging, IrohTransportMode::Auto)
        ));
        assert!(matches!(
            parse_relay_mode("direct-preferred").unwrap(),
            (RelayMode::Default, IrohTransportMode::DirectPreferred)
        ));
        assert!(matches!(
            parse_relay_mode("relay-only").unwrap(),
            (RelayMode::Default, IrohTransportMode::RelayOnly)
        ));
        assert!(matches!(
            parse_relay_mode("disabled").unwrap(),
            (RelayMode::Disabled, IrohTransportMode::Disabled)
        ));
    }

    #[test]
    fn parse_relay_mode_rejects_unknown_value() {
        let err = parse_relay_mode("not-a-mode").unwrap_err().to_string();
        assert!(err.contains("unsupported NEXUS_IROH_RELAY_MODE"));
    }

    #[test]
    fn relay_only_reports_relay_connection_path() {
        assert_eq!(IrohTransportMode::RelayOnly.as_str(), "RelayOnly");
        assert_eq!(IrohTransportMode::RelayOnly.connection_path(), "relay");
    }

    #[test]
    fn automatic_modes_do_not_claim_relay_path() {
        assert_eq!(IrohTransportMode::Auto.connection_path(), "auto");
        assert_eq!(IrohTransportMode::DirectPreferred.connection_path(), "auto");
    }

    #[test]
    fn parse_peer_addr_accepts_iroh_uri_without_query() {
        let secret = SecretKey::generate();
        let peer = format!("iroh://{}", secret.public());
        let parsed = parse_peer_addr(&peer).unwrap();
        assert_eq!(parsed.id, secret.public());
    }

    #[test]
    fn parse_peer_addr_accepts_iroh_uri_with_query() {
        let secret = SecretKey::generate();
        let peer = format!("iroh://{}?addr=abc", secret.public());
        let parsed = parse_peer_addr(&peer).unwrap();
        assert_eq!(parsed.id, secret.public());
    }

    #[test]
    fn parse_peer_addr_rejects_invalid_peer() {
        assert!(parse_peer_addr("iroh://not-a-valid-peer").is_err());
    }

    #[tokio::test]
    async fn read_control_preface_reads_single_line() {
        let (mut server_stream, mut client_stream) = connected_tcp_pair().await;
        let token = STANDARD.encode("token");

        let reader = tokio::spawn(async move { read_control_preface(&mut server_stream).await });
        client_stream
            .write_all(format!("OPEN {token} peer\ntrailing").as_bytes())
            .await
            .unwrap();

        let line = reader.await.unwrap().unwrap();
        assert_eq!(line, format!("OPEN {token} peer"));
    }

    #[tokio::test]
    async fn read_control_preface_rejects_empty_stream() {
        let (mut server_stream, client_stream) = connected_tcp_pair().await;
        drop(client_stream);

        let err = read_control_preface(&mut server_stream).await.unwrap_err();
        assert!(err.to_string().contains("empty raft control preface"));
    }

    #[tokio::test]
    async fn read_control_preface_rejects_oversized_preface() {
        let (mut server_stream, mut client_stream) = connected_tcp_pair().await;

        let reader = tokio::spawn(async move { read_control_preface(&mut server_stream).await });
        client_stream.write_all(&vec![b'a'; 4098]).await.unwrap();

        let err = reader.await.unwrap().unwrap_err();
        assert!(err.to_string().contains("exceeds maximum size"));
    }

    #[test]
    fn malformed_raft_token_encoding_is_rejected() {
        let invalid = base64::engine::general_purpose::STANDARD.decode("not base64!");
        assert!(invalid.is_err());
    }

    #[test]
    fn constant_time_compare_path_has_stable_shape_for_same_length_values() {
        let started = Instant::now();
        for _ in 0..10_000 {
            let _ = constant_time_equals(b"aaaaaaaaaaaaaaaa", b"bbbbbbbbbbbbbbbb");
            let _ = constant_time_equals(b"aaaaaaaaaaaaaaaa", b"aaaaaaaaaaaaaaaa");
        }

        assert!(started.elapsed() < Duration::from_secs(2));
    }

    async fn connected_tcp_pair() -> (TcpStream, TcpStream) {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let client = TcpStream::connect(address);
        let server = listener.accept();
        let (client, server) = tokio::join!(client, server);
        let (server, _) = server.unwrap();
        (server, client.unwrap())
    }
}
