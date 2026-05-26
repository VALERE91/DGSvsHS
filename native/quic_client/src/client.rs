
use std::net::ToSocketAddrs;
use std::sync::atomic::{AtomicI32, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};

use bytes::{Buf, BufMut, BytesMut};
use quinn::{Connection, Endpoint, RecvStream, SendStream};
use rustls::client::{ServerCertVerified, ServerCertVerifier};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::runtime::Builder;
use tokio::sync::mpsc;
use tracing::{debug, error, warn};

use crate::wire;

#[repr(i32)]
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum ConnectionState {
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Disconnecting = 3,
}

impl ConnectionState {
    fn as_i32(self) -> i32 {
        self as i32
    }
}

#[derive(Debug)]
pub struct InboundMessage {
    pub msg_type: u8,
    pub payload: Vec<u8>,
}

/// Commands issued by the calling thread to the async backend.
enum Command {
    Connect { host: String, port: u16 },
    SendReliable { msg_type: u8, payload: Vec<u8> },
    SendUnreliable { msg_type: u8, payload: Vec<u8> },
    Shutdown,
}

pub struct Client {
    cmd_tx: mpsc::UnboundedSender<Command>,
    event_rx: Mutex<mpsc::UnboundedReceiver<InboundMessage>>,
    state: Arc<AtomicI32>,
    rtt_ms: Arc<AtomicU32>, // f32 bits — read with f32::from_bits
    _thread: Option<JoinHandle<()>>,
}

impl Client {
    pub fn new() -> Self {
        let (cmd_tx, cmd_rx) = mpsc::unbounded_channel::<Command>();
        let (event_tx, event_rx) = mpsc::unbounded_channel::<InboundMessage>();

        let state = Arc::new(AtomicI32::new(ConnectionState::Disconnected.as_i32()));
        let rtt_ms = Arc::new(AtomicU32::new(60f32.to_bits()));

        let state_clone = state.clone();
        let rtt_clone = rtt_ms.clone();

        let thread = thread::Builder::new()
            .name("dgsvshs-quic".into())
            .spawn(move || {
                let rt = match Builder::new_current_thread().enable_all().build() {
                    Ok(rt) => rt,
                    Err(e) => {
                        error!("failed to start tokio runtime: {e}");
                        return;
                    }
                };
                rt.block_on(async move {
                    backend_loop(cmd_rx, event_tx, state_clone, rtt_clone).await;
                });
            })
            .expect("spawning tokio thread");

        Self {
            cmd_tx,
            event_rx: Mutex::new(event_rx),
            state,
            rtt_ms,
            _thread: Some(thread),
        }
    }

    pub fn connect(&self, host: &str, port: u16) -> Result<(), &'static str> {
        self.cmd_tx
            .send(Command::Connect { host: host.to_string(), port })
            .map_err(|_| "backend shut down")
    }

    pub fn send(&self, msg_type: u8, data: &[u8]) -> Result<(), &'static str> {
        if data.len() > wire::MAX_PAYLOAD_BYTES {
            return Err("payload exceeds MAX_PAYLOAD_BYTES");
        }
        let cmd = if wire::is_reliable(msg_type) {
            Command::SendReliable { msg_type, payload: data.to_vec() }
        } else {
            Command::SendUnreliable { msg_type, payload: data.to_vec() }
        };
        self.cmd_tx.send(cmd).map_err(|_| "backend shut down")
    }

    pub fn poll(&self) -> Option<InboundMessage> {
        self.event_rx.lock().ok()?.try_recv().ok()
    }

    pub fn state(&self) -> ConnectionState {
        match self.state.load(Ordering::Acquire) {
            0 => ConnectionState::Disconnected,
            1 => ConnectionState::Connecting,
            2 => ConnectionState::Connected,
            3 => ConnectionState::Disconnecting,
            _ => ConnectionState::Disconnected,
        }
    }

    pub fn rtt_ms(&self) -> f32 {
        f32::from_bits(self.rtt_ms.load(Ordering::Acquire))
    }
}

impl Drop for Client {
    fn drop(&mut self) {
        let _ = self.cmd_tx.send(Command::Shutdown);
        // The thread will exit when the runtime drops at the end of its block_on. We don't join
        // here — the runtime is a current-thread one driving a single connection, and waiting
        // could block the caller for an arbitrary amount of time during a graceful close.
    }
}

// =========================================================================================
//                                   Tokio backend
// =========================================================================================

async fn backend_loop(
    mut cmd_rx: mpsc::UnboundedReceiver<Command>,
    event_tx: mpsc::UnboundedSender<InboundMessage>,
    state: Arc<AtomicI32>,
    rtt_ms: Arc<AtomicU32>,
) {
    // Active connection state. None until Connect is processed.
    let mut conn: Option<Connection> = None;
    let mut reliable_send: Option<SendStream> = None;

    while let Some(cmd) = cmd_rx.recv().await {
        match cmd {
            Command::Connect { host, port } => {
                state.store(ConnectionState::Connecting.as_i32(), Ordering::Release);
                match open_connection(&host, port).await {
                    Ok((c, send, recv)) => {
                        debug!("connected to {host}:{port}");
                        // Spawn reader tasks (stream + datagrams) that push InboundMessage into event_tx.
                        spawn_stream_reader(recv, event_tx.clone());
                        spawn_datagram_reader(c.clone(), event_tx.clone());
                        spawn_rtt_sampler(c.clone(), rtt_ms.clone());

                        conn = Some(c);
                        reliable_send = Some(send);
                        state.store(ConnectionState::Connected.as_i32(), Ordering::Release);
                    }
                    Err(e) => {
                        error!("connect failed: {e}");
                        state.store(ConnectionState::Disconnected.as_i32(), Ordering::Release);
                    }
                }
            }

            Command::SendReliable { msg_type, payload } => {
                let Some(send) = reliable_send.as_mut() else {
                    warn!("send-reliable dropped: no active stream");
                    continue;
                };
                if let Err(e) = write_reliable(send, msg_type, &payload).await {
                    error!("reliable send failed: {e}");
                    state.store(ConnectionState::Disconnected.as_i32(), Ordering::Release);
                    conn = None;
                    reliable_send = None;
                }
            }

            Command::SendUnreliable { msg_type, payload } => {
                let Some(c) = conn.as_ref() else {
                    warn!("send-unreliable dropped: no active connection");
                    continue;
                };
                if let Err(e) = write_datagram(c, msg_type, &payload) {
                    error!("datagram send failed: {e}");
                    // Datagrams can fail for non-fatal reasons (overflow buffer, MTU). Don't tear
                    // down the connection on a single drop.
                }
            }

            Command::Shutdown => {
                state.store(ConnectionState::Disconnecting.as_i32(), Ordering::Release);
                if let Some(c) = conn.take() {
                    c.close(0u32.into(), b"client shutdown");
                }
                reliable_send = None;
                state.store(ConnectionState::Disconnected.as_i32(), Ordering::Release);
                break;
            }
        }
    }
}

async fn open_connection(
    host: &str,
    port: u16,
) -> Result<(Connection, SendStream, RecvStream), Box<dyn std::error::Error + Send + Sync>> {
    let client_config = make_client_config();

    // Bind a UDP socket on any local port for our outgoing QUIC. quinn 0.10 wants a SocketAddr;
    // 0.0.0.0:0 lets the OS pick.
    let mut endpoint = Endpoint::client("0.0.0.0:0".parse()?)?;
    endpoint.set_default_client_config(client_config);

    let remote = (host, port)
        .to_socket_addrs()?
        .next()
        .ok_or("no addresses resolved")?;

    let connection = endpoint.connect(remote, host)?.await?;

    let (send, recv) = connection.open_bi().await?;

    Ok((connection, send, recv))
}

fn make_client_config() -> quinn::ClientConfig {
    let crypto = rustls::ClientConfig::builder()
        .with_safe_defaults()
        .with_custom_certificate_verifier(SkipServerVerification::new())
        .with_no_client_auth();

    let mut client_config = quinn::ClientConfig::new(Arc::new(crypto));

    let mut transport = quinn::TransportConfig::default();
    transport.datagram_receive_buffer_size(Some(1024 * 1024));
    transport.keep_alive_interval(Some(std::time::Duration::from_secs(1)));
    transport.initial_rtt(std::time::Duration::from_millis(15));
    client_config.transport_config(Arc::new(transport));

    client_config
}

struct SkipServerVerification;
impl SkipServerVerification {
    fn new() -> Arc<Self> { Arc::new(Self) }
}
impl ServerCertVerifier for SkipServerVerification {
    fn verify_server_cert(
        &self,
        _end_entity: &rustls::Certificate,
        _intermediates: &[rustls::Certificate],
        _server_name: &rustls::ServerName,
        _scts: &mut dyn Iterator<Item = &[u8]>,
        _ocsp_response: &[u8],
        _now: std::time::SystemTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        Ok(ServerCertVerified::assertion())
    }
}

// ----- Stream framing -----

async fn write_reliable(
    send: &mut SendStream,
    msg_type: u8,
    payload: &[u8],
) -> Result<(), quinn::WriteError> {
    let mut frame = BytesMut::with_capacity(4 + 1 + payload.len());
    frame.put_u32_le(payload.len() as u32);
    frame.put_u8(msg_type);
    frame.put_slice(payload);
    send.write_all(&frame).await
}

fn write_datagram(conn: &Connection, msg_type: u8, payload: &[u8]) -> Result<(), quinn::SendDatagramError> {
    let mut frame = BytesMut::with_capacity(1 + payload.len());
    frame.put_u8(msg_type);
    frame.put_slice(payload);
    conn.send_datagram(frame.freeze())
}

fn spawn_stream_reader(mut recv: RecvStream, event_tx: mpsc::UnboundedSender<InboundMessage>) {
    tokio::spawn(async move {
        loop {
            // [u32 LE length][u8 msg_type][payload]
            let len = match recv.read_u32_le().await {
                Ok(l) => l as usize,
                Err(_) => break,
            };
            if len > wire::MAX_PAYLOAD_BYTES {
                warn!("oversize stream frame ({len} B) — closing reader");
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
            if event_tx.send(InboundMessage { msg_type, payload }).is_err() {
                break;
            }
        }
    });
}

fn spawn_datagram_reader(conn: Connection, event_tx: mpsc::UnboundedSender<InboundMessage>) {
    tokio::spawn(async move {
        while let Ok(bytes) = conn.read_datagram().await {
            if bytes.is_empty() {
                continue;
            }
            let mut b = bytes;
            let msg_type = b.get_u8();
            let payload = b.to_vec();
            if event_tx.send(InboundMessage { msg_type, payload }).is_err() {
                break;
            }
        }
    });
}

fn spawn_rtt_sampler(conn: Connection, rtt_ms: Arc<AtomicU32>) {
    tokio::spawn(async move {
        let mut interval = tokio::time::interval(std::time::Duration::from_millis(250));
        loop {
            interval.tick().await;
            let rtt = conn.rtt();
            let ms = rtt.as_secs_f32() * 1000.0;
            rtt_ms.store(ms.to_bits(), Ordering::Release);
        }
    });
}
