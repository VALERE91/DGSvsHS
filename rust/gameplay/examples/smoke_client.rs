// End-to-end smoke test for the QUIC + wire-format stack.
// Run the Bevy server in one terminal (`cargo run -p cli`), then in another:
//   cargo run --example smoke_client -p gameplay
//
// Validates: QUIC handshake (incl. stateless retry + version negotiation),
// stream-based reliable control framing (one-stream-per-message, FIN-
// terminated), wire-codec ClientHello / ServerWelcome round-trip, slot
// assignment.

use std::error::Error;
use std::net::{SocketAddr, UdpSocket};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use gameplay::network::{
    read_server_welcome, write_client_hello, MSG_CLIENT_HELLO, MSG_SERVER_WELCOME,
    PROTOCOL_VERSION, R,
};

const SERVER_ADDR: &str = "127.0.0.1:4433";
const ALPN: &[u8] = b"dgs/v1";
const MAX_DATAGRAM_SIZE: usize = 1350;
const RECV_BUF: usize = 65535;
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(10);

fn main() -> Result<(), Box<dyn Error>> {
    let server_addr: SocketAddr = SERVER_ADDR.parse()?;

    let mut config = quiche::Config::new(quiche::PROTOCOL_VERSION)?;
    config.set_application_protos(&[ALPN])?;
    // Self-signed dev cert on the server side; trust unconditionally for trials.
    config.verify_peer(false);
    config.set_max_idle_timeout(10_000);
    config.set_max_recv_udp_payload_size(MAX_DATAGRAM_SIZE);
    config.set_max_send_udp_payload_size(MAX_DATAGRAM_SIZE);
    config.set_initial_max_data(256 * 1024);
    config.set_initial_max_stream_data_bidi_local(64 * 1024);
    config.set_initial_max_stream_data_bidi_remote(64 * 1024);
    config.set_initial_max_stream_data_uni(64 * 1024);
    config.set_initial_max_streams_bidi(4);
    config.set_initial_max_streams_uni(4);
    config.enable_dgram(true, 256, 256);
    config.set_disable_active_migration(true);

    let socket = UdpSocket::bind("0.0.0.0:0")?;
    socket.set_nonblocking(true)?;
    let local_addr = socket.local_addr()?;

    let scid_bytes = mint_scid_bytes();
    let scid = quiche::ConnectionId::from_ref(&scid_bytes);
    let mut conn =
        quiche::connect(Some("dgsvshs"), &scid, local_addr, server_addr, &mut config)?;

    let mut buf = vec![0u8; RECV_BUF];
    let mut out = vec![0u8; MAX_DATAGRAM_SIZE];

    // Kick off the handshake by sending the initial flight.
    pump_outgoing(&mut conn, &socket, &mut out)?;

    let stream_id: u64 = 0; // first client-initiated bidirectional stream
    let mut hello_sent = false;
    let mut welcome_seen = false;
    let mut welcome_buf = Vec::with_capacity(64);
    let deadline = Instant::now() + HANDSHAKE_TIMEOUT;

    while Instant::now() < deadline && !welcome_seen {
        // Drain UDP.
        loop {
            match socket.recv_from(&mut buf) {
                Ok((n, from)) => {
                    let recv_info = quiche::RecvInfo {
                        from,
                        to: local_addr,
                    };
                    if let Err(e) = conn.recv(&mut buf[..n], recv_info) {
                        eprintln!("[Client] conn.recv: {:?}", e);
                    }
                }
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => break,
                Err(e) => return Err(e.into()),
            }
        }

        // Once the QUIC handshake is up, ship ClientHello on stream 0 with FIN.
        if conn.is_established() && !hello_sent {
            let mut payload = vec![MSG_CLIENT_HELLO];
            write_client_hello(&mut payload, 0);
            conn.stream_send(stream_id, &payload, true)?;
            println!(
                "[Client] handshake OK, sent ClientHello on stream {} ({} bytes)",
                stream_id,
                payload.len()
            );
            hello_sent = true;
        }

        // Drain readable streams. Server replies on the same bidi stream with FIN.
        let readable: Vec<u64> = conn.readable().collect();
        for sid in readable {
            let mut sbuf = [0u8; 256];
            loop {
                let (n, fin) = match conn.stream_recv(sid, &mut sbuf) {
                    Ok(v) => v,
                    Err(quiche::Error::Done) => break,
                    Err(e) => {
                        eprintln!("[Client] stream_recv {}: {:?}", sid, e);
                        break;
                    }
                };
                if n > 0 {
                    welcome_buf.extend_from_slice(&sbuf[..n]);
                }
                if fin {
                    if welcome_buf.is_empty() {
                        eprintln!("[Client] stream {} fin with no payload", sid);
                        break;
                    }
                    let msg_type = welcome_buf[0];
                    println!(
                        "[Client] stream {} complete: {} bytes, msg_type=0x{:02x}",
                        sid,
                        welcome_buf.len(),
                        msg_type
                    );
                    if msg_type != MSG_SERVER_WELCOME {
                        return Err(format!(
                            "expected ServerWelcome (0x{:02x}), got 0x{:02x}",
                            MSG_SERVER_WELCOME, msg_type
                        )
                        .into());
                    }
                    let mut r = R::new(&welcome_buf[1..]);
                    let sw = read_server_welcome(&mut r)?;
                    println!(
                        "[Client] ServerWelcome v={} player_id={} server_tick={} sim_tick_ms={} snap_every={}",
                        sw.version,
                        sw.player_id,
                        sw.server_tick,
                        sw.sim_tick_ms,
                        sw.snapshot_every_n_ticks
                    );
                    if sw.version != PROTOCOL_VERSION {
                        return Err(format!(
                            "protocol version mismatch: server {} vs client {}",
                            sw.version, PROTOCOL_VERSION
                        )
                        .into());
                    }
                    welcome_seen = true;
                    welcome_buf.clear();
                    break;
                }
            }
        }

        pump_outgoing(&mut conn, &socket, &mut out)?;

        std::thread::sleep(Duration::from_millis(10));
    }

    if !welcome_seen {
        return Err("timed out waiting for ServerWelcome".into());
    }

    // Close cleanly so the server reaps us promptly.
    conn.close(true, 0, b"smoke ok")?;
    pump_outgoing(&mut conn, &socket, &mut out)?;

    println!("[Client] OK");
    Ok(())
}

fn pump_outgoing(
    conn: &mut quiche::Connection,
    socket: &UdpSocket,
    out: &mut [u8],
) -> Result<(), Box<dyn Error>> {
    loop {
        let (n, send_info) = match conn.send(out) {
            Ok(v) => v,
            Err(quiche::Error::Done) => return Ok(()),
            Err(e) => return Err(e.into()),
        };
        socket.send_to(&out[..n], send_info.to)?;
    }
}

fn mint_scid_bytes() -> [u8; quiche::MAX_CONN_ID_LEN] {
    let mut bytes = [0u8; quiche::MAX_CONN_ID_LEN];
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0);
    bytes[..8].copy_from_slice(&nanos.to_be_bytes());
    bytes
}
