use std::ffi::CStr;
use std::os::raw::{c_char, c_int};

use crate::client::Client;

/// Create a new client. Returns an opaque handle to be passed to all subsequent calls.
/// Returns null on allocation failure (extremely rare).
#[no_mangle]
pub unsafe extern "C" fn dgs_client_create() -> *mut Client {
    let client = Client::new();
    Box::into_raw(Box::new(client))
}

/// Destroy a client handle. After this call, the pointer is invalid; do not pass it to anything.
/// Safe to call with a null pointer (no-op).
#[no_mangle]
pub unsafe extern "C" fn dgs_client_destroy(h: *mut Client) {
    if h.is_null() {
        return;
    }
    drop(Box::from_raw(h));
}

/// Begin a connection to `host:port`. Non-blocking. Watch `dgs_client_state` for transitions.
/// Returns 0 on dispatched, -1 on error (null handle, invalid host string, backend stopped).
#[no_mangle]
pub unsafe extern "C" fn dgs_client_connect(h: *mut Client, host: *const c_char, port: u16) -> c_int {
    let Some(client) = h.as_ref() else {
        return -1;
    };
    if host.is_null() {
        return -1;
    }
    let host_str = match CStr::from_ptr(host).to_str() {
        Ok(s) => s,
        Err(_) => return -1,
    };
    match client.connect(host_str, port) {
        Ok(()) => 0,
        Err(_) => -1,
    }
}

/// Returns the current connection state as the same i32 the C# `ConnectionState` enum expects
/// (Disconnected=0, Connecting=1, Connected=2, Disconnecting=3). Returns -1 if `h` is null.
#[no_mangle]
pub unsafe extern "C" fn dgs_client_state(h: *mut Client) -> c_int {
    let Some(client) = h.as_ref() else {
        return -1;
    };
    client.state() as c_int
}

/// Returns the smoothed RTT in milliseconds. Returns 0 if no measurement yet or `h` is null.
#[no_mangle]
pub unsafe extern "C" fn dgs_client_rtt_ms(h: *mut Client) -> f32 {
    let Some(client) = h.as_ref() else {
        return 0.0;
    };
    client.rtt_ms()
}

/// Send one outbound message. `msg_type` is the wire-format tag (see `WireFormat.md §1`); the
/// runtime decides reliable-stream vs datagram based on it. `data` is copied into an internal
/// buffer before return, so the caller may free/reuse immediately.
///
/// Returns 0 on dispatched, -1 on error (null handle, oversize payload, backend stopped).
#[no_mangle]
pub unsafe extern "C" fn dgs_client_send(
    h: *mut Client,
    msg_type: u8,
    data: *const u8,
    len: c_int,
) -> c_int {
    let Some(client) = h.as_ref() else {
        return -1;
    };
    if data.is_null() || len < 0 {
        return -1;
    }
    let slice = std::slice::from_raw_parts(data, len as usize);
    match client.send(msg_type, slice) {
        Ok(()) => 0,
        Err(_) => -1,
    }
}

/// Drain one inbound message into the caller's buffer. Non-blocking.
///
/// Returns:
/// - `> 0` — number of bytes written into `buf`. `out_msg_type` is set.
/// - `= 0` — no messages pending.
/// - `< 0` — error (null args, buffer too small, backend stopped). `out_msg_type` is left untouched.
#[no_mangle]
pub unsafe extern "C" fn dgs_client_poll(
    h: *mut Client,
    out_msg_type: *mut u8,
    buf: *mut u8,
    buf_len: c_int,
) -> c_int {
    let Some(client) = h.as_ref() else {
        return -1;
    };
    if out_msg_type.is_null() || buf.is_null() || buf_len < 0 {
        return -1;
    }

    let Some(msg) = client.poll() else {
        return 0;
    };
    if msg.payload.len() > buf_len as usize {
        // Caller buffer too small. The message is consumed (lost) since the C# layer's contract
        // doesn't have a peek+resize step. Caller should always pass MAX_PAYLOAD_BYTES-sized buf.
        return -1;
    }

    *out_msg_type = msg.msg_type;
    let dst = std::slice::from_raw_parts_mut(buf, msg.payload.len());
    dst.copy_from_slice(&msg.payload);
    msg.payload.len() as c_int
}
