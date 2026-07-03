// QUIC *server* for DGSvsHS — the mirror of `client.rs`. Same `dgsvshs_socket`
// cdylib, same wire framing (reliable stream: [u32 LE len][u8 tag][payload];
// unreliable: [u8 tag][payload]), same ALPN `dgsvshs/2`. Lets the Unity DOTS (DGS)
// server run QUIC like every other leg instead of NGO/UDP, so the transport +
// per-packet crypto workload is identical across the comparison.
//
// Multi-client: assigns each connection a slot id 0..MAX_PLAYERS-1 (matching the
// gameplay player-slot model). The C# side polls events (connected / disconnected /
// message) and sends per-client via the FFI in `ffi.rs`.

use std::collections::HashMap;
use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use bytes::{Buf, BufMut, BytesMut};
use quinn::{Connection, Endpoint, RecvStream, SendStream, ServerConfig};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::runtime::Builder;
use tokio::sync::mpsc;
use tracing::{debug, error, warn};

use crate::wire;

const MAX_PLAYERS: u8 = 4;

/// Event kinds surfaced to the FFI poll (matches C# QuicNetworkServer).
pub mod event_kind {
    pub const NONE: u8 = 0;
    pub const CONNECTED: u8 = 1;
    pub const DISCONNECTED: u8 = 2;
    pub const MESSAGE: u8 = 3;
}

pub struct ServerEvent {
    pub kind: u8,
    pub client_id: u8,
    pub msg_type: u8,
    pub payload: Vec<u8>,
}

/// One outbound message to a specific client, resolved to that connection's writer task.
struct OutMsg {
    reliable: bool,
    msg_type: u8,
    payload: Vec<u8>,
}

enum Command {
    Start { port: u16 },
    Send { client_id: u8, msg: OutMsg },
    Shutdown,
}

/// Slot allocator + per-client outbound channels. Only ever locked for brief,
/// await-free critical sections.
struct Slots {
    out: HashMap<u8, mpsc::UnboundedSender<OutMsg>>,
}
impl Slots {
    fn alloc(&self) -> Option<u8> {
        (0..MAX_PLAYERS).find(|id| !self.out.contains_key(id))
    }
}

pub struct Server {
    cmd_tx: mpsc::UnboundedSender<Command>,
    event_rx: Mutex<mpsc::UnboundedReceiver<ServerEvent>>,
    listening: Arc<AtomicU8>,
    _thread: Option<JoinHandle<()>>,
}

impl Server {
    pub fn new() -> Self {
        let (cmd_tx, cmd_rx) = mpsc::unbounded_channel::<Command>();
        let (event_tx, event_rx) = mpsc::unbounded_channel::<ServerEvent>();
        let listening = Arc::new(AtomicU8::new(0));
        let listening_clone = listening.clone();

        let thread = thread::Builder::new()
            .name("dgsvshs-quic-server".into())
            .spawn(move || {
                let rt = match Builder::new_multi_thread().worker_threads(2).enable_all().build() {
                    Ok(rt) => rt,
                    Err(e) => {
                        error!("server tokio runtime failed: {e}");
                        return;
                    }
                };
                rt.block_on(async move {
                    backend_loop(cmd_rx, event_tx, listening_clone).await;
                });
            })
            .expect("spawn server tokio thread");

        Self {
            cmd_tx,
            event_rx: Mutex::new(event_rx),
            listening,
            _thread: Some(thread),
        }
    }

    pub fn start(&self, port: u16) -> Result<(), &'static str> {
        self.cmd_tx.send(Command::Start { port }).map_err(|_| "backend shut down")
    }

    pub fn send(&self, client_id: u8, msg_type: u8, data: &[u8]) -> Result<(), &'static str> {
        if data.len() > wire::MAX_PAYLOAD_BYTES {
            return Err("payload exceeds MAX_PAYLOAD_BYTES");
        }
        let reliable = wire::is_reliable(msg_type);
        self.cmd_tx
            .send(Command::Send {
                client_id,
                msg: OutMsg { reliable, msg_type, payload: data.to_vec() },
            })
            .map_err(|_| "backend shut down")
    }

    pub fn poll(&self) -> Option<ServerEvent> {
        self.event_rx.lock().ok()?.try_recv().ok()
    }

    pub fn is_listening(&self) -> bool {
        self.listening.load(Ordering::Acquire) == 1
    }
}

impl Drop for Server {
    fn drop(&mut self) {
        let _ = self.cmd_tx.send(Command::Shutdown);
    }
}

// =========================================================================================
//                                   Tokio backend
// =========================================================================================

async fn backend_loop(
    mut cmd_rx: mpsc::UnboundedReceiver<Command>,
    event_tx: mpsc::UnboundedSender<ServerEvent>,
    listening: Arc<AtomicU8>,
) {
    let slots = Arc::new(Mutex::new(Slots { out: HashMap::new() }));
    let mut endpoint: Option<Endpoint> = None;

    while let Some(cmd) = cmd_rx.recv().await {
        match cmd {
            Command::Start { port } => match make_endpoint(port) {
                Ok(ep) => {
                    spawn_accept_loop(ep.clone(), event_tx.clone(), slots.clone());
                    endpoint = Some(ep);
                    listening.store(1, Ordering::Release);
                    debug!("QUIC server listening on 0.0.0.0:{port}");
                }
                Err(e) => {
                    error!("server bind on port {port} failed: {e}");
                    listening.store(0, Ordering::Release);
                }
            },

            Command::Send { client_id, msg } => {
                let tx = slots.lock().unwrap().out.get(&client_id).cloned();
                if let Some(tx) = tx {
                    let _ = tx.send(msg);
                }
                // else: client already gone — drop silently.
            }

            Command::Shutdown => {
                if let Some(ep) = endpoint.take() {
                    ep.close(0u32.into(), b"server shutdown");
                }
                listening.store(0, Ordering::Release);
                break;
            }
        }
    }
}

fn make_endpoint(port: u16) -> Result<Endpoint, Box<dyn std::error::Error + Send + Sync>> {
    let server_config = make_server_config()?;
    let addr = format!("0.0.0.0:{port}").parse()?;
    let endpoint = Endpoint::server(server_config, addr)?;
    Ok(endpoint)
}

fn make_server_config() -> Result<ServerConfig, Box<dyn std::error::Error + Send + Sync>> {
    // Self-signed cert — the Unity client skips verification (SkipServerVerification in
    // client.rs), so this is only to satisfy TLS 1.3, not for identity.
    let cert = rcgen::generate_simple_self_signed(vec!["dgsvshs".to_string()])?;
    let cert_der = rustls::Certificate(cert.serialize_der()?);
    let key_der = rustls::PrivateKey(cert.serialize_private_key_der());

    let mut crypto = rustls::ServerConfig::builder()
        .with_safe_defaults()
        .with_no_client_auth()
        .with_single_cert(vec![cert_der], key_der)?;
    crypto.alpn_protocols = vec![b"dgsvshs/2".to_vec()];

    let mut server_config = ServerConfig::with_crypto(Arc::new(crypto));
    let mut transport = quinn::TransportConfig::default();
    transport.datagram_receive_buffer_size(Some(1024 * 1024));
    transport.keep_alive_interval(Some(Duration::from_secs(1)));
    server_config.transport_config(Arc::new(transport));
    Ok(server_config)
}

fn spawn_accept_loop(
    endpoint: Endpoint,
    event_tx: mpsc::UnboundedSender<ServerEvent>,
    slots: Arc<Mutex<Slots>>,
) {
    tokio::spawn(async move {
        while let Some(connecting) = endpoint.accept().await {
            let event_tx = event_tx.clone();
            let slots = slots.clone();
            tokio::spawn(async move {
                match connecting.await {
                    Ok(conn) => handle_connection(conn, event_tx, slots).await,
                    Err(e) => warn!("incoming connection failed handshake: {e}"),
                }
            });
        }
    });
}

async fn handle_connection(
    conn: Connection,
    event_tx: mpsc::UnboundedSender<ServerEvent>,
    slots: Arc<Mutex<Slots>>,
) {
    // The client opens a bi stream right after connecting (client.rs open_bi).
    let (send, recv) = match conn.accept_bi().await {
        Ok(s) => s,
        Err(e) => {
            warn!("accept_bi failed: {e}");
            return;
        }
    };

    // Assign a slot + register the outbound channel.
    let (out_tx, out_rx) = mpsc::unbounded_channel::<OutMsg>();
    let client_id = {
        let mut g = slots.lock().unwrap();
        match g.alloc() {
            Some(id) => {
                g.out.insert(id, out_tx);
                id
            }
            None => {
                conn.close(1u32.into(), b"server full");
                return;
            }
        }
    };

    let _ = event_tx.send(ServerEvent {
        kind: event_kind::CONNECTED,
        client_id,
        msg_type: 0,
        payload: Vec::new(),
    });

    let writer = tokio::spawn(writer_task(send, conn.clone(), out_rx));
    let reader_stream = tokio::spawn(stream_reader(recv, client_id, event_tx.clone()));
    let reader_dgram = tokio::spawn(datagram_reader(conn.clone(), client_id, event_tx.clone()));

    // Block until the connection closes, then clean up + free the slot.
    let _ = conn.closed().await;
    debug!("client {client_id} disconnected");
    slots.lock().unwrap().out.remove(&client_id);
    writer.abort();
    reader_stream.abort();
    reader_dgram.abort();

    let _ = event_tx.send(ServerEvent {
        kind: event_kind::DISCONNECTED,
        client_id,
        msg_type: 0,
        payload: Vec::new(),
    });
}

async fn writer_task(mut send: SendStream, conn: Connection, mut out_rx: mpsc::UnboundedReceiver<OutMsg>) {
    while let Some(msg) = out_rx.recv().await {
        if msg.reliable {
            let mut frame = BytesMut::with_capacity(4 + 1 + msg.payload.len());
            frame.put_u32_le(msg.payload.len() as u32);
            frame.put_u8(msg.msg_type);
            frame.put_slice(&msg.payload);
            if send.write_all(&frame).await.is_err() {
                break;
            }
        } else {
            let mut frame = BytesMut::with_capacity(1 + msg.payload.len());
            frame.put_u8(msg.msg_type);
            frame.put_slice(&msg.payload);
            // Datagram sends can fail transiently (congestion) — don't tear down the conn.
            let _ = conn.send_datagram(frame.freeze());
        }
    }
}

async fn stream_reader(mut recv: RecvStream, client_id: u8, event_tx: mpsc::UnboundedSender<ServerEvent>) {
    loop {
        // [u32 LE length][u8 msg_type][payload]
        let len = match recv.read_u32_le().await {
            Ok(l) => l as usize,
            Err(_) => break,
        };
        if len > wire::MAX_PAYLOAD_BYTES {
            warn!("oversize stream frame ({len} B) from client {client_id} — closing reader");
            break;
        }
        let msg_type = match recv.read_u8().await {
            Ok(b) => b,
            Err(_) => break,
        };
        let mut payload = vec![0u8; len];
        if recv.read_exact(&mut payload).await.is_err() {
            break;
        }
        if event_tx
            .send(ServerEvent { kind: event_kind::MESSAGE, client_id, msg_type, payload })
            .is_err()
        {
            break;
        }
    }
}

async fn datagram_reader(conn: Connection, client_id: u8, event_tx: mpsc::UnboundedSender<ServerEvent>) {
    while let Ok(bytes) = conn.read_datagram().await {
        if bytes.is_empty() {
            continue;
        }
        let mut b = bytes;
        let msg_type = b.get_u8();
        let payload = b.to_vec();
        if event_tx
            .send(ServerEvent { kind: event_kind::MESSAGE, client_id, msg_type, payload })
            .is_err()
        {
            break;
        }
    }
}
