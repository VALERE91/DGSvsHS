# dgsvshs_quic_client

QUIC client library for the DGSvsHS project. Built once in Rust, consumed from two surfaces:

- **C ABI** — Unity's `QuicNetworkClient.cs` loads the resulting `.dll`/`.so`/`.dylib` via
  `[DllImport("dgsvshs_socket")]` and calls the seven `dgs_client_*` functions defined in
  `src/ffi.rs`.
- **Rust API** — `dgsvshs_socket::Client::{new, connect, send, poll, state, rtt_ms}` for any
  Rust caller (future Bevy-side test harnesses, the Arch-server tooling, etc.).

The cdylib + rlib dual `crate-type` means both consumers come out of one `cargo build`.

## Wire contract

Reads and writes the wire format defined in `DGSvsHS/Assets/_Game/Net/WireFormat.md`. The crate
itself doesn't serialize/deserialize message bodies — the caller passes pre-encoded payloads
plus a one-byte tag. The crate uses the tag to decide reliable-stream vs QUIC-datagram routing
(see `src/wire.rs::is_reliable`):

| Tag    | Name           | Lane                              |
|--------|----------------|-----------------------------------|
| `0x01` | ClientHello    | Reliable (bidi stream)            |
| `0x02` | ServerWelcome  | Reliable (bidi stream) — incoming |
| `0x10` | Input          | Unreliable (datagram)             |
| `0x20` | Snapshot       | Unreliable (datagram) — incoming  |
| `0xF0` | Disconnect     | Reliable (bidi stream)            |

Reliable framing on the stream: `[u32 LE length][u8 msg_type][payload]`. Datagram framing:
`[u8 msg_type][payload]` (QUIC datagrams are message-bounded, no length needed).

## Building

Requirements: Rust 1.75+ (stable). No platform-specific deps — `quinn` + `rustls` are pure-Rust
QUIC and TLS. No OpenSSL, no MsQuic, nothing to install.

### Windows

```powershell
.\scripts\build_and_deploy.ps1
```

Produces `target/release/dgsvshs_socket.dll` and copies it into
`DGSvsHS/Assets/Plugins/x86_64/`. Unity picks up the new DLL on next editor focus.

Manual build (if you want to verify or customize the output location):

```powershell
cargo build --release --lib
# Output: target/release/dgsvshs_socket.dll
```

### Linux / macOS

```bash
./scripts/build_and_deploy.sh
```

Produces `libdgsvshs_socket.so` (Linux) or `libdgsvshs_socket.dylib` (macOS) and copies it
into the Unity plugins folder. Unity 6 picks them up on next focus.

Manual:

```bash
cargo build --release --lib
# Output:
#   Linux:  target/release/libdgsvshs_socket.so
#   macOS:  target/release/libdgsvshs_socket.dylib
```

### Cross-compiling

To build a Linux binary from Windows (or macOS from Linux, etc.) use [`cross`](https://github.com/cross-rs/cross):

```bash
cargo install cross
cross build --release --lib --target x86_64-unknown-linux-gnu
```

The output ends up under `target/<triple>/release/`.

## Unity plugin layout

Drop the produced library into `DGSvsHS/Assets/Plugins/x86_64/` (or whichever architecture
folder matches your build target). The Unity Plugin Inspector should automatically set the
correct CPU + platform restrictions on first import; re-check them if not.

Unity loads natives by base name (`dgsvshs_socket`), not extension, so you can ship the same
project to all three desktop platforms by placing the platform-specific binary in the same
folder under its conventional name (`dgsvshs_socket.dll`, `libdgsvshs_socket.so`,
`libdgsvshs_socket.dylib`).

## Rust usage

```rust
use dgsvshs_socket::{Client, ConnectionState};

let client = Client::new();
client.connect("127.0.0.1", 7777).unwrap();

// Spin until connected. In a real app, poll on a timer.
while client.state() != ConnectionState::Connected {
    std::thread::sleep(std::time::Duration::from_millis(10));
}

// Send a ClientHello (tag 0x01) — payload is wire-encoded by the caller.
client.send(0x01, &[0x03, 0x00, 0x00, 0x00, 0x00]).unwrap();

// Drain inbound.
while let Some(msg) = client.poll() {
    eprintln!("rx tag=0x{:02X} ({} bytes)", msg.msg_type, msg.payload.len());
}
```

## Internal architecture

```
┌──────────────────────────┐
│  Caller (Unity / Rust)   │
└─────────────┬────────────┘
              │ mpsc commands (Connect / SendReliable / SendUnreliable / Shutdown)
              ▼
┌──────────────────────────┐
│ Tokio current-thread rt  │   spawned in a dedicated OS thread on Client::new()
│ ├─ stream reader task    │ ── inbound reliable msgs ───┐
│ ├─ datagram reader task  │ ── inbound unreliable msgs ─┤
│ └─ rtt sampler task      │ ── updates atomic rtt_ms    │
└─────────────┬────────────┘                             │
              │ quinn::Endpoint                          │ mpsc events
              ▼                                          │
        QUIC wire ↔ remote server                        │
                                                         ▼
                                            ┌──────────────────────┐
                                            │ Caller poll() drain  │
                                            └──────────────────────┘
```

- Each `Client` owns one Tokio current-thread runtime on a dedicated OS thread. No global
  runtime — multiple `Client` instances are independent.
- The caller thread (Unity main thread or any Rust thread) only ever touches `Client` via the
  synchronous handle methods, which are non-blocking mpsc sends / try-recvs.
- TLS cert verification is disabled (`SkipServerVerification`) for development. For production
  swap in a real `ServerCertVerifier`.

## License

MIT OR Apache-2.0 (your choice).
