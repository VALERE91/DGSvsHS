
pub mod tag {
    pub const CLIENT_HELLO: u8 = 0x01;
    pub const SERVER_WELCOME: u8 = 0x02;
    pub const INPUT: u8 = 0x10;
    pub const SNAPSHOT: u8 = 0x20;
    pub const DISCONNECT: u8 = 0xF0;
}

pub fn is_reliable(msg_type: u8) -> bool {
    match msg_type {
        tag::CLIENT_HELLO | tag::SERVER_WELCOME | tag::DISCONNECT => true,
        tag::INPUT | tag::SNAPSHOT => false,
        _ => true,
    }
}

pub const MAX_PAYLOAD_BYTES: usize = 64 * 1024;
