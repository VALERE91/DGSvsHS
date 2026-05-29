use std::net::SocketAddr;

use bevy::prelude::*;

#[derive(Copy, Clone, Eq, PartialEq, Hash, Debug)]
pub struct ClientId(pub u64);

#[derive(Clone, Debug)]
pub enum MsgKind {
    Datagram,
    Stream { id: u64, fin: bool },
}

#[derive(Message, Clone, Debug)]
pub struct NetMsgIn {
    pub client: ClientId,
    pub kind: MsgKind,
    pub payload: Vec<u8>,
}

#[derive(Message, Clone, Debug)]
pub struct NetMsgOut {
    pub client: ClientId,
    pub kind: MsgKind,
    pub payload: Vec<u8>,
}

#[derive(Message, Clone, Debug)]
pub struct ClientConnected {
    pub client: ClientId,
    pub peer: SocketAddr,
}

#[derive(Message, Clone, Debug)]
pub struct ClientDisconnected {
    pub client: ClientId,
    pub peer: SocketAddr,
}