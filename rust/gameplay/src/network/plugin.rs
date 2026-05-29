use std::collections::HashMap;
use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use bevy::log::{error, info, warn};
use bevy::prelude::*;
use quiche::{Config, Connection, ConnectionId, Header, RecvInfo, Type};

use super::cert;
use super::codec::{
    read_client_hello, read_input_batch, write_server_welcome, MSG_CLIENT_HELLO, MSG_INPUT,
    MSG_SERVER_WELCOME, PROTOCOL_VERSION, R,
};
use super::events::{ClientConnected, ClientDisconnected, ClientId, MsgKind, NetMsgIn, NetMsgOut};
use crate::game::constants::MAX_PLAYERS;

const ALPN: &[u8] = b"dgs/v1";
const LISTEN_ADDR: &str = "0.0.0.0:4433";
const MAX_DATAGRAM_SIZE: usize = 1350;
const RECV_BUF: usize = 65535;
const STREAM_CHUNK: usize = 4096;

// Tight per-connection windows — 4 concurrent players in a 128 MB VM.
const INITIAL_MAX_DATA: u64 = 256 * 1024;
const INITIAL_MAX_STREAM_DATA: u64 = 64 * 1024;
const MAX_IDLE_TIMEOUT_MS: u64 = 30_000;

const RETRY_TOKEN_MAGIC: &[u8] = b"dgsvshs";

pub struct NetworkPlugin;

impl Plugin for NetworkPlugin {
    fn build(&self, app: &mut App) {
        let certs = cert::write_to_tmp().expect("write QUIC cert/key to tmp");
        let config = build_config(&certs);
        let (socket, local) = bind();
        info!("[QUIC] listening on {}", local);

        app.insert_resource(QuicSocket { socket, local })
            .insert_resource(PlayerSlots::default())
            .insert_resource(ServerTick::default())
            .insert_non_send_resource(QuicConfig(config))
            .insert_non_send_resource(QuicConnections::default())
            .add_message::<NetMsgIn>()
            .add_message::<NetMsgOut>()
            .add_message::<ClientConnected>()
            .add_message::<ClientDisconnected>()
            .add_systems(PreUpdate, (quic_tick_timeouts, quic_recv).chain())
            .add_systems(Update, (tick_server_tick, dispatch_inbound, handle_disconnect))
            .add_systems(PostUpdate, (quic_send_app, quic_send).chain());
    }
}

#[derive(Resource, Default)]
pub struct ServerTick(pub u32);

#[derive(Resource, Default)]
pub struct PlayerSlots {
    slots: [Option<ClientId>; MAX_PLAYERS],
}

impl PlayerSlots {
    fn assign(&mut self, client: ClientId) -> Option<u8> {
        // Re-use existing slot if this client is already assigned (idempotent
        // for retransmitted ClientHello on the same connection).
        if let Some(idx) = self.slots.iter().position(|s| *s == Some(client)) {
            return Some(idx as u8);
        }
        let idx = self.slots.iter().position(Option::is_none)?;
        self.slots[idx] = Some(client);
        Some(idx as u8)
    }
    fn release(&mut self, client: ClientId) {
        for s in self.slots.iter_mut() {
            if *s == Some(client) {
                *s = None;
            }
        }
    }
}

#[derive(Resource)]
struct QuicSocket {
    socket: UdpSocket,
    local: SocketAddr,
}

// quiche::Config and Connection are Send but not Sync — kept as NonSend so
// Bevy schedules them on the main thread without a Mutex.
struct QuicConfig(Config);

#[derive(Default)]
struct QuicConnections {
    // Primary store, keyed by the SCID we issued on accept.
    map: HashMap<ConnectionId<'static>, ConnState>,
    // Routing index: every active CID known to a conn → its primary key.
    // Refreshed from Connection::source_ids() after each recv, so any new
    // SCIDs issued by quiche via NEW_CONNECTION_ID are picked up.
    cid_to_primary: HashMap<ConnectionId<'static>, ConnectionId<'static>>,
    // Game-facing handle → primary key, for outbound dispatch.
    client_to_cid: HashMap<ClientId, ConnectionId<'static>>,
    scid_counter: u64,
    client_id_counter: u64,
}

struct ConnState {
    conn: Connection,
    peer: SocketAddr,
    client_id: ClientId,
    next_timeout: Option<Instant>,
    established_emitted: bool,
}

fn build_config(certs: &cert::CertFiles) -> Config {
    let mut cfg = Config::new(quiche::PROTOCOL_VERSION).expect("quiche::Config::new");
    cfg.load_cert_chain_from_pem_file(certs.cert.to_str().unwrap())
        .expect("load cert");
    cfg.load_priv_key_from_pem_file(certs.key.to_str().unwrap())
        .expect("load key");
    cfg.set_application_protos(&[ALPN]).expect("set ALPN");
    cfg.set_max_idle_timeout(MAX_IDLE_TIMEOUT_MS);
    cfg.set_max_recv_udp_payload_size(MAX_DATAGRAM_SIZE);
    cfg.set_max_send_udp_payload_size(MAX_DATAGRAM_SIZE);
    cfg.set_initial_max_data(INITIAL_MAX_DATA);
    cfg.set_initial_max_stream_data_bidi_local(INITIAL_MAX_STREAM_DATA);
    cfg.set_initial_max_stream_data_bidi_remote(INITIAL_MAX_STREAM_DATA);
    cfg.set_initial_max_stream_data_uni(INITIAL_MAX_STREAM_DATA);
    cfg.set_initial_max_streams_bidi(4);
    cfg.set_initial_max_streams_uni(4);
    cfg.enable_dgram(true, 256, 256);
    cfg.set_disable_active_migration(true);
    cfg
}

fn bind() -> (UdpSocket, SocketAddr) {
    let addr: SocketAddr = LISTEN_ADDR.parse().unwrap();
    let socket = UdpSocket::bind(addr).expect("bind UDP 4433");
    socket.set_nonblocking(true).expect("set non-blocking");
    let local = socket.local_addr().expect("local addr");
    (socket, local)
}

fn quic_tick_timeouts(mut conns: NonSendMut<QuicConnections>) {
    let now = Instant::now();
    for state in conns.map.values_mut() {
        if let Some(deadline) = state.next_timeout {
            if now >= deadline {
                state.conn.on_timeout();
            }
        }
    }
}

fn quic_recv(
    socket: Res<QuicSocket>,
    mut config: NonSendMut<QuicConfig>,
    mut conns: NonSendMut<QuicConnections>,
    mut msg_in_evw: MessageWriter<NetMsgIn>,
    mut connected_evw: MessageWriter<ClientConnected>,
) {
    let mut buf = [0u8; RECV_BUF];
    let mut out = [0u8; MAX_DATAGRAM_SIZE];
    // Reborrow once so field-level disjoint borrows (map vs cid_to_primary)
    // bypass the smart-pointer Deref through NonSendMut.
    let conns: &mut QuicConnections = &mut conns;

    // Drain the UDP socket, routing or accepting per packet.
    loop {
        let (len, from) = match socket.socket.recv_from(&mut buf) {
            Ok(v) => v,
            Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => break,
            Err(e) => {
                error!("[QUIC] recv_from: {}", e);
                break;
            }
        };

        let (dcid, scid_hdr, token, hdr_ty, hdr_version) = {
            let hdr = match Header::from_slice(&mut buf[..len], quiche::MAX_CONN_ID_LEN) {
                Ok(h) => h,
                Err(e) => {
                    error!("[QUIC] header: {:?}", e);
                    continue;
                }
            };
            (
                hdr.dcid.clone().into_owned(),
                hdr.scid.clone().into_owned(),
                hdr.token.clone().unwrap_or_default(),
                hdr.ty,
                hdr.version,
            )
        };

        let primary = conns.cid_to_primary.get(&dcid).cloned();

        if let Some(primary_cid) = primary {
            let state = conns.map.get_mut(&primary_cid).unwrap();
            let recv_info = RecvInfo {
                from,
                to: socket.local,
            };
            if let Err(e) = state.conn.recv(&mut buf[..len], recv_info) {
                error!("[QUIC] recv: {:?}", e);
                continue;
            }
            state.next_timeout = state.conn.timeout().map(|d| Instant::now() + d);
            sync_cids(&mut conns.cid_to_primary, &primary_cid, &state.conn);
            continue;
        }

        if hdr_ty != Type::Initial {
            continue;
        }

        if !quiche::version_is_supported(hdr_version) {
            info!(
                "[QUIC] unsupported version 0x{:x} from {}",
                hdr_version, from
            );
            match quiche::negotiate_version(&scid_hdr, &dcid, &mut out) {
                Ok(n) => {
                    let _ = socket.socket.send_to(&out[..n], from);
                }
                Err(e) => error!("[QUIC] negotiate_version: {:?}", e),
            }
            continue;
        }

        if token.is_empty() {
            conns.scid_counter += 1;
            let new_scid = mint_scid(conns.scid_counter);
            let new_token = mint_token(&dcid, &from);
            match quiche::retry(
                &scid_hdr,
                &dcid,
                &new_scid,
                &new_token,
                hdr_version,
                &mut out,
            ) {
                Ok(n) => {
                    let _ = socket.socket.send_to(&out[..n], from);
                }
                Err(e) => error!("[QUIC] retry: {:?}", e),
            }
            continue;
        }

        let odcid = match validate_token(&from, &token) {
            Some(o) => o,
            None => {
                error!("[QUIC] bad retry token from {}", from);
                continue;
            }
        };

        // After Retry, hdr.dcid is the SCID we proposed — reuse it as our
        // primary key so cid_to_primary stays self-consistent.
        let scid = dcid.clone();
        let conn = match quiche::accept(&scid, Some(&odcid), socket.local, from, &mut config.0) {
            Ok(c) => c,
            Err(e) => {
                error!("[QUIC] accept: {:?}", e);
                continue;
            }
        };

        conns.client_id_counter += 1;
        let client_id = ClientId(conns.client_id_counter);

        info!(
            "[QUIC] new conn scid={} from={} client={}",
            hex(&scid),
            from,
            client_id.0
        );

        conns.map.insert(
            scid.clone(),
            ConnState {
                conn,
                peer: from,
                client_id,
                next_timeout: None,
                established_emitted: false,
            },
        );
        conns.cid_to_primary.insert(scid.clone(), scid.clone());
        conns.client_to_cid.insert(client_id, scid.clone());

        let state = conns.map.get_mut(&scid).unwrap();
        let recv_info = RecvInfo {
            from,
            to: socket.local,
        };
        if let Err(e) = state.conn.recv(&mut buf[..len], recv_info) {
            error!("[QUIC] initial recv: {:?}", e);
        }
        state.next_timeout = state.conn.timeout().map(|d| Instant::now() + d);
        sync_cids(&mut conns.cid_to_primary, &scid, &state.conn);
    }

    // After UDP drain, walk every connection once: emit handshake-completion
    // events and surface app-layer DATAGRAM / stream chunks.
    let mut sbuf = [0u8; STREAM_CHUNK];
    let mut dbuf = [0u8; MAX_DATAGRAM_SIZE];
    for state in conns.map.values_mut() {
        if state.conn.is_established() && !state.established_emitted {
            info!(
                "[QUIC] established client={} from={}",
                state.client_id.0, state.peer
            );
            connected_evw.write(ClientConnected {
                client: state.client_id,
                peer: state.peer,
            });
            state.established_emitted = true;
        }

        loop {
            match state.conn.dgram_recv(&mut dbuf) {
                Ok(n) => {
                    msg_in_evw.write(NetMsgIn {
                        client: state.client_id,
                        kind: MsgKind::Datagram,
                        payload: dbuf[..n].to_vec(),
                    });
                }
                Err(quiche::Error::Done) => break,
                Err(e) => {
                    error!("[QUIC] dgram_recv: {:?}", e);
                    break;
                }
            }
        }

        // Snapshot stream ids first — readable() borrows the connection.
        let stream_ids: Vec<u64> = state.conn.readable().collect();
        for stream_id in stream_ids {
            loop {
                let (n, fin) = match state.conn.stream_recv(stream_id, &mut sbuf) {
                    Ok(v) => v,
                    Err(quiche::Error::Done) => break,
                    Err(e) => {
                        error!("[QUIC] stream_recv {}: {:?}", stream_id, e);
                        break;
                    }
                };
                if n > 0 {
                    msg_in_evw.write(NetMsgIn {
                        client: state.client_id,
                        kind: MsgKind::Stream { id: stream_id, fin },
                        payload: sbuf[..n].to_vec(),
                    });
                }
                if fin {
                    break;
                }
            }
        }
    }
}

fn quic_send_app(
    mut out_msgs: MessageReader<NetMsgOut>,
    mut conns: NonSendMut<QuicConnections>,
) {
    let conns: &mut QuicConnections = &mut conns;
    for msg in out_msgs.read() {
        let cid = match conns.client_to_cid.get(&msg.client) {
            Some(c) => c.clone(),
            None => {
                error!("[QUIC] outbound to unknown client {}", msg.client.0);
                continue;
            }
        };
        let state = match conns.map.get_mut(&cid) {
            Some(s) => s,
            None => continue,
        };
        match &msg.kind {
            MsgKind::Datagram => {
                if let Err(e) = state.conn.dgram_send(&msg.payload) {
                    error!("[QUIC] dgram_send: {:?}", e);
                }
            }
            MsgKind::Stream { id, fin } => match state.conn.stream_send(*id, &msg.payload, *fin) {
                Ok(n) if n < msg.payload.len() => {
                    warn!(
                        "[QUIC] stream_send partial {}/{} stream={}",
                        n,
                        msg.payload.len(),
                        id
                    );
                }
                Ok(_) => {}
                Err(e) => error!("[QUIC] stream_send: {:?}", e),
            },
        }
    }
}

fn quic_send(
    socket: Res<QuicSocket>,
    mut conns: NonSendMut<QuicConnections>,
    mut disc_evw: MessageWriter<ClientDisconnected>,
) {
    let conns: &mut QuicConnections = &mut conns;
    let mut out = [0u8; MAX_DATAGRAM_SIZE];
    for state in conns.map.values_mut() {
        loop {
            let (n, send_info) = match state.conn.send(&mut out) {
                Ok(v) => v,
                Err(quiche::Error::Done) => break,
                Err(e) => {
                    error!("[QUIC] conn.send: {:?}", e);
                    let _ = state.conn.close(false, 0x1, b"send_failed");
                    break;
                }
            };
            if let Err(e) = socket.socket.send_to(&out[..n], send_info.to) {
                if e.kind() == std::io::ErrorKind::WouldBlock {
                    break;
                }
                error!("[QUIC] socket send: {}", e);
                break;
            }
        }
        state.next_timeout = state.conn.timeout().map(|d| Instant::now() + d);
    }

    let closed: Vec<ConnectionId<'static>> = conns
        .map
        .iter()
        .filter(|(_, s)| s.conn.is_closed())
        .map(|(k, _)| k.clone())
        .collect();
    for cid in closed {
        if let Some(s) = conns.map.remove(&cid) {
            info!("[QUIC] closed client={} peer={}", s.client_id.0, s.peer);
            conns.client_to_cid.remove(&s.client_id);
            disc_evw.write(ClientDisconnected {
                client: s.client_id,
                peer: s.peer,
            });
        }
        conns.cid_to_primary.retain(|_, primary| primary != &cid);
    }
}

fn sync_cids(
    cid_to_primary: &mut HashMap<ConnectionId<'static>, ConnectionId<'static>>,
    primary: &ConnectionId<'static>,
    conn: &Connection,
) {
    for cid in conn.source_ids() {
        let owned = cid.clone().into_owned();
        cid_to_primary
            .entry(owned)
            .or_insert_with(|| primary.clone());
    }
}

fn mint_scid(counter: u64) -> ConnectionId<'static> {
    let mut bytes = [0u8; quiche::MAX_CONN_ID_LEN];
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0);
    bytes[..8].copy_from_slice(&nanos.to_be_bytes());
    bytes[8..16].copy_from_slice(&counter.to_be_bytes());
    ConnectionId::from_ref(&bytes).into_owned()
}

// NOT cryptographically secure — anyone observing the format can forge a
// token. Standard quiche-example pattern; acceptable for trial workloads
// where the only attacker would be the operator's own machine.
fn mint_token(orig_dcid: &ConnectionId<'_>, src: &SocketAddr) -> Vec<u8> {
    let mut token = Vec::with_capacity(64);
    token.extend_from_slice(RETRY_TOKEN_MAGIC);
    match src.ip() {
        IpAddr::V4(a) => token.extend_from_slice(&a.octets()),
        IpAddr::V6(a) => token.extend_from_slice(&a.octets()),
    }
    token.extend_from_slice(orig_dcid.as_ref());
    token
}

fn validate_token(src: &SocketAddr, token: &[u8]) -> Option<ConnectionId<'static>> {
    if token.len() < RETRY_TOKEN_MAGIC.len()
        || &token[..RETRY_TOKEN_MAGIC.len()] != RETRY_TOKEN_MAGIC
    {
        return None;
    }
    let rest = &token[RETRY_TOKEN_MAGIC.len()..];
    let addr: Vec<u8> = match src.ip() {
        IpAddr::V4(a) => a.octets().to_vec(),
        IpAddr::V6(a) => a.octets().to_vec(),
    };
    if rest.len() < addr.len() || &rest[..addr.len()] != addr.as_slice() {
        return None;
    }
    Some(ConnectionId::from_ref(&rest[addr.len()..]).into_owned())
}

fn tick_server_tick(mut t: ResMut<ServerTick>) {
    t.0 = t.0.wrapping_add(1);
}

// Inbound msg_type dispatch. Wire framing: first payload byte = msg_type,
// rest = codec payload. Reliable control messages arrive on one fresh stream
// per message (FIN-terminated). Unreliable game messages arrive as DATAGRAMs.
//
// For the smoke milestone we only handle ClientHello → ServerWelcome and
// log Input. Sim plumbing comes in a later phase.
//
// Stream chunking caveat: a logical message may arrive across multiple
// `NetMsgIn { kind: Stream { .. } }` events if it exceeds the per-call read
// buffer. ClientHello (5 bytes) is single-chunk; revisit when we add larger
// reliable messages.
fn dispatch_inbound(
    mut inbound: MessageReader<NetMsgIn>,
    mut outbound: MessageWriter<NetMsgOut>,
    mut slots: ResMut<PlayerSlots>,
    server_tick: Res<ServerTick>,
) {
    for msg in inbound.read() {
        if msg.payload.is_empty() {
            continue;
        }
        let msg_type = msg.payload[0];
        let mut r = R::new(&msg.payload[1..]);

        match msg_type {
            MSG_CLIENT_HELLO => {
                let (version, _caps) = match read_client_hello(&mut r) {
                    Ok(v) => v,
                    Err(e) => {
                        error!(
                            "[Proto] ClientHello decode from client {}: {:?}",
                            msg.client.0, e
                        );
                        continue;
                    }
                };
                if version != PROTOCOL_VERSION {
                    error!(
                        "[Proto] ClientHello version mismatch from client {}: got {}, want {}",
                        msg.client.0, version, PROTOCOL_VERSION
                    );
                    continue;
                }
                let Some(slot) = slots.assign(msg.client) else {
                    error!("[Proto] no free player slot for client {}", msg.client.0);
                    continue;
                };
                info!(
                    "[Proto] ClientHello v{} from client {} → slot {}",
                    version, msg.client.0, slot
                );

                let mut welcome = vec![MSG_SERVER_WELCOME];
                write_server_welcome(&mut welcome, slot, server_tick.0);

                // Reply on the same stream (if it was a stream) with FIN so
                // the client knows the message is complete.
                let reply_kind = match msg.kind {
                    MsgKind::Stream { id, .. } => MsgKind::Stream { id, fin: true },
                    MsgKind::Datagram => MsgKind::Datagram,
                };
                outbound.write(NetMsgOut {
                    client: msg.client,
                    kind: reply_kind,
                    payload: welcome,
                });
            }

            MSG_INPUT => {
                let mut cmds = Vec::with_capacity(4);
                match read_input_batch(&mut r, &mut cmds) {
                    Ok(n) => info!(
                        "[Proto] Input batch n={} client={} newest_tick={}",
                        n, msg.client.0, cmds[0].tick
                    ),
                    Err(e) => error!("[Proto] Input decode: {:?}", e),
                }
            }

            other => {
                warn!(
                    "[Proto] unknown msg_type 0x{:02x} from client {}",
                    other, msg.client.0
                );
            }
        }
    }
}

fn handle_disconnect(
    mut events: MessageReader<ClientDisconnected>,
    mut slots: ResMut<PlayerSlots>,
) {
    for e in events.read() {
        slots.release(e.client);
        info!("[Proto] released slot for client {}", e.client.0);
    }
}

fn hex(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push_str(&format!("{:02x}", b));
    }
    s
}