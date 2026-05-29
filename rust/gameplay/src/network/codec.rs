// Mirror of DGSvsHS/Assets/_Game/Net/WireCodec.cs — v3 protocol.
// All byte ordering: little-endian. Float = IEEE 754 binary32.
// Quantization rules: see DGSvsHS/Assets/_Game/Net/WireFormat.md §9.

#![allow(dead_code)]

use crate::game::constants::*;
use crate::game::{
    EnemyDeltaEntry, EnemySnap, FireEvent, InputCmd, InputFlags, PlayerSnap, RoundPhase, Snapshot,
    SnapshotKind,
};

pub const PROTOCOL_VERSION: u32 = 3;

pub const MSG_CLIENT_HELLO: u8 = 0x01;
pub const MSG_SERVER_WELCOME: u8 = 0x02;
pub const MSG_INPUT: u8 = 0x10;
pub const MSG_SNAPSHOT: u8 = 0x20;
pub const MSG_DISCONNECT: u8 = 0xF0;

// Fixed wire sizes — kept in sync with WireFormat.md §4.4 / WireCodec.cs.
pub const SNAPSHOT_HEADER_BYTES: usize = 1 + 4 + 4 + 4 + 2 + 4 + 4 + 1; // 24
pub const PLAYER_SNAP_FULL_BYTES: usize = 1 + 2 + 2 + 2 + 1 + 2; // 10
pub const ENEMY_SNAP_FULL_BYTES: usize = 2 + 2 + 2; // 6
pub const ENEMY_DELTA_ENTRY_BYTES: usize = 2 + 2 + 2; // 6
pub const FIRE_EVENT_BYTES: usize = 4 + 1 + 2 + 2 + 2 + 2 + 1; // 14
pub const INPUT_CMD_WIRE_BYTES: usize = 4 + 4 + 4 * 4 + 1; // 25

#[derive(Debug, PartialEq, Eq)]
pub enum DecodeError {
    Truncated,
    BadEnum(&'static str, u8),
    OutOfRange(&'static str),
}

impl std::fmt::Display for DecodeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            DecodeError::Truncated => write!(f, "wire payload truncated"),
            DecodeError::BadEnum(name, b) => write!(f, "invalid {} discriminant: 0x{:02x}", name, b),
            DecodeError::OutOfRange(what) => write!(f, "out of range: {}", what),
        }
    }
}

impl std::error::Error for DecodeError {}

// ---------- Quantization ----------

fn quant_pos(meters: f32) -> i16 {
    let q = (meters * POSITION_SCALE as f32).round() as i32;
    q.clamp(i16::MIN as i32, i16::MAX as i32) as i16
}

fn dequant_pos(q: i16) -> f32 {
    q as f32 / POSITION_SCALE as f32
}

fn quant_angle(dir_x: f32, dir_y: f32) -> i16 {
    let a = if dir_x * dir_x + dir_y * dir_y > 0.0001 {
        dir_y.atan2(dir_x)
    } else {
        0.0
    };
    let q = (a * ANGLE_SCALE as f32).round() as i32;
    q.clamp(i16::MIN as i32, i16::MAX as i32) as i16
}

fn dequant_angle(q: i16) -> (f32, f32) {
    let a = q as f32 / ANGLE_SCALE as f32;
    (a.cos(), a.sin())
}

fn quant_disable(seconds: f32) -> u16 {
    let t = (seconds * TICKS_PER_SECOND).round() as i32;
    t.clamp(0, u16::MAX as i32) as u16
}

fn dequant_disable(t: u16) -> f32 {
    t as f32 / TICKS_PER_SECOND
}

/// True iff the quantized position of `current` differs from `baseline`.
/// Drives delta change-detection — entries that round to the same i16 are
/// considered unchanged and must NOT be emitted on the wire.
pub fn enemy_position_changed(baseline: &EnemySnap, current: &EnemySnap) -> bool {
    quant_pos(baseline.pos_x) != quant_pos(current.pos_x)
        || quant_pos(baseline.pos_y) != quant_pos(current.pos_y)
}

// ---------- Byte I/O ----------

pub struct W<'a> {
    buf: &'a mut Vec<u8>,
}

impl<'a> W<'a> {
    pub fn new(buf: &'a mut Vec<u8>) -> Self {
        Self { buf }
    }
    pub fn u8(&mut self, v: u8) {
        self.buf.push(v);
    }
    pub fn u16(&mut self, v: u16) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }
    pub fn u32(&mut self, v: u32) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }
    pub fn i16(&mut self, v: i16) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }
    pub fn f32(&mut self, v: f32) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }
}

pub struct R<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> R<'a> {
    pub fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }
    pub fn remaining(&self) -> usize {
        self.buf.len().saturating_sub(self.pos)
    }
    pub fn position(&self) -> usize {
        self.pos
    }

    fn take(&mut self, n: usize) -> Result<&[u8], DecodeError> {
        if self.remaining() < n {
            return Err(DecodeError::Truncated);
        }
        let s = &self.buf[self.pos..self.pos + n];
        self.pos += n;
        Ok(s)
    }

    pub fn u8(&mut self) -> Result<u8, DecodeError> {
        Ok(self.take(1)?[0])
    }
    pub fn u16(&mut self) -> Result<u16, DecodeError> {
        let s = self.take(2)?;
        Ok(u16::from_le_bytes([s[0], s[1]]))
    }
    pub fn u32(&mut self) -> Result<u32, DecodeError> {
        let s = self.take(4)?;
        Ok(u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
    }
    pub fn i16(&mut self) -> Result<i16, DecodeError> {
        let s = self.take(2)?;
        Ok(i16::from_le_bytes([s[0], s[1]]))
    }
    pub fn f32(&mut self) -> Result<f32, DecodeError> {
        let s = self.take(4)?;
        Ok(f32::from_le_bytes([s[0], s[1], s[2], s[3]]))
    }
}

// ---------- ClientHello (0x01) ----------

pub fn write_client_hello(buf: &mut Vec<u8>, capabilities: u8) {
    let mut w = W::new(buf);
    w.u32(PROTOCOL_VERSION);
    w.u8(capabilities);
}

pub fn read_client_hello(r: &mut R) -> Result<(u32, u8), DecodeError> {
    let v = r.u32()?;
    let c = r.u8()?;
    Ok((v, c))
}

// ---------- ServerWelcome (0x02) ----------

pub fn write_server_welcome(buf: &mut Vec<u8>, player_id: u8, server_tick: u32) {
    let mut w = W::new(buf);
    w.u32(PROTOCOL_VERSION);
    w.u8(player_id);
    w.u32(server_tick);
    w.u16(SIM_TICK_MS as u16);
    w.u16(SNAPSHOT_EVERY_N_TICKS as u16);
}

#[derive(Copy, Clone, Debug)]
pub struct ServerWelcome {
    pub version: u32,
    pub player_id: u8,
    pub server_tick: u32,
    pub sim_tick_ms: u16,
    pub snapshot_every_n_ticks: u16,
}

pub fn read_server_welcome(r: &mut R) -> Result<ServerWelcome, DecodeError> {
    Ok(ServerWelcome {
        version: r.u32()?,
        player_id: r.u8()?,
        server_tick: r.u32()?,
        sim_tick_ms: r.u16()?,
        snapshot_every_n_ticks: r.u16()?,
    })
}

// ---------- Input (0x10) ----------

pub fn write_input_batch(buf: &mut Vec<u8>, cmds: &[InputCmd]) {
    let count = cmds.len();
    assert!(
        (1..=4).contains(&count),
        "input batch count out of range: {}",
        count
    );
    let mut w = W::new(buf);
    w.u8(count as u8);
    for cmd in cmds {
        write_one_input(&mut w, cmd);
    }
}

/// Returns the number of inputs read and appends them to `out`.
pub fn read_input_batch(r: &mut R, out: &mut Vec<InputCmd>) -> Result<usize, DecodeError> {
    let count = r.u8()?;
    if !(1..=4).contains(&count) {
        return Err(DecodeError::OutOfRange("input batch count"));
    }
    for _ in 0..count {
        out.push(read_one_input(r)?);
    }
    Ok(count as usize)
}

fn write_one_input(w: &mut W, cmd: &InputCmd) {
    w.u32(cmd.tick);
    w.u32(cmd.last_acked_server_tick);
    w.f32(cmd.move_x);
    w.f32(cmd.move_y);
    w.f32(cmd.aim_x);
    w.f32(cmd.aim_y);
    w.u8(cmd.flags.0);
}

fn read_one_input(r: &mut R) -> Result<InputCmd, DecodeError> {
    Ok(InputCmd {
        tick: r.u32()?,
        last_acked_server_tick: r.u32()?,
        move_x: r.f32()?,
        move_y: r.f32()?,
        aim_x: r.f32()?,
        aim_y: r.f32()?,
        flags: InputFlags(r.u8()?),
    })
}

// ---------- Snapshot (0x20): header ----------

pub fn write_snapshot_header(buf: &mut Vec<u8>, s: &Snapshot) {
    let mut w = W::new(buf);
    w.u8(s.kind as u8);
    w.u32(s.tick);
    w.u32(s.baseline_tick);
    w.u32(s.last_processed_input_tick);
    w.u16(s.round);
    w.f32(s.round_timer);
    w.f32(s.inter_round_timer);
    w.u8(s.phase as u8);
}

#[derive(Copy, Clone, Debug)]
pub struct SnapshotHeader {
    pub kind: SnapshotKind,
    pub tick: u32,
    pub baseline_tick: u32,
    pub last_processed_input_tick: u32,
    pub round: u16,
    pub round_timer: f32,
    pub inter_round_timer: f32,
    pub phase: RoundPhase,
}

pub fn read_snapshot_header(r: &mut R) -> Result<SnapshotHeader, DecodeError> {
    let kind = match r.u8()? {
        0 => SnapshotKind::Full,
        1 => SnapshotKind::Delta,
        x => return Err(DecodeError::BadEnum("SnapshotKind", x)),
    };
    Ok(SnapshotHeader {
        kind,
        tick: r.u32()?,
        baseline_tick: r.u32()?,
        last_processed_input_tick: r.u32()?,
        round: r.u16()?,
        round_timer: r.f32()?,
        inter_round_timer: r.f32()?,
        phase: phase_from_u8(r.u8()?)?,
    })
}

fn phase_from_u8(b: u8) -> Result<RoundPhase, DecodeError> {
    match b {
        0 => Ok(RoundPhase::PreGame),
        1 => Ok(RoundPhase::InRound),
        2 => Ok(RoundPhase::InterRound),
        3 => Ok(RoundPhase::Victory),
        4 => Ok(RoundPhase::Defeat),
        x => Err(DecodeError::BadEnum("RoundPhase", x)),
    }
}

// ---------- Snapshot: per-entity ----------

fn write_player_snap_full(w: &mut W, p: &PlayerSnap) {
    w.u8(p.id);
    w.i16(quant_pos(p.pos_x));
    w.i16(quant_pos(p.pos_y));
    w.i16(quant_angle(p.aim_x, p.aim_y));
    w.u8(if p.alive { 1 } else { 0 });
    w.u16(quant_disable(p.disable_timer));
}

fn read_player_snap_full(r: &mut R) -> Result<PlayerSnap, DecodeError> {
    let id = r.u8()?;
    let px = r.i16()?;
    let py = r.i16()?;
    let aim = r.i16()?;
    let alive = r.u8()?;
    let dt = r.u16()?;
    let (aim_x, aim_y) = dequant_angle(aim);
    Ok(PlayerSnap {
        id,
        pos_x: dequant_pos(px),
        pos_y: dequant_pos(py),
        aim_x,
        aim_y,
        alive: alive != 0,
        disable_timer: dequant_disable(dt),
    })
}

fn write_enemy_snap_full(w: &mut W, e: &EnemySnap) {
    w.u16(e.id);
    w.i16(quant_pos(e.pos_x));
    w.i16(quant_pos(e.pos_y));
}

fn read_enemy_snap_full(r: &mut R) -> Result<EnemySnap, DecodeError> {
    let id = r.u16()?;
    let px = r.i16()?;
    let py = r.i16()?;
    Ok(EnemySnap {
        id,
        pos_x: dequant_pos(px),
        pos_y: dequant_pos(py),
    })
}

fn write_fire_event(w: &mut W, f: &FireEvent) {
    w.u32(f.tick);
    w.u8(f.shooter_id);
    w.i16(quant_pos(f.origin_x));
    w.i16(quant_pos(f.origin_y));
    w.i16(quant_angle(f.dir_x, f.dir_y));
    w.i16(quant_pos(f.distance));
    w.u8(f.kill_count);
}

fn read_fire_event(r: &mut R) -> Result<FireEvent, DecodeError> {
    let tick = r.u32()?;
    let sid = r.u8()?;
    let ox = r.i16()?;
    let oy = r.i16()?;
    let da = r.i16()?;
    let dist = r.i16()?;
    let kills = r.u8()?;
    let (dir_x, dir_y) = dequant_angle(da);
    Ok(FireEvent {
        tick,
        shooter_id: sid,
        origin_x: dequant_pos(ox),
        origin_y: dequant_pos(oy),
        dir_x,
        dir_y,
        distance: dequant_pos(dist),
        kill_count: kills,
    })
}

// ---------- Snapshot: full body ----------

pub fn write_full_snapshot_body(
    buf: &mut Vec<u8>,
    players: &[PlayerSnap],
    enemies: &[EnemySnap],
    enemy_total_in_world: u32,
    fires: &[FireEvent],
) {
    let mut w = W::new(buf);
    let pcount = players.len().min(u8::MAX as usize);
    w.u8(pcount as u8);
    for p in &players[..pcount] {
        write_player_snap_full(&mut w, p);
    }

    let ecount = enemies.len().min(u16::MAX as usize);
    w.u16(ecount as u16);
    w.u32(enemy_total_in_world);
    for e in &enemies[..ecount] {
        write_enemy_snap_full(&mut w, e);
    }

    let fcount = fires.len().min(16);
    w.u8(fcount as u8);
    for f in &fires[..fcount] {
        write_fire_event(&mut w, f);
    }
}

pub fn read_full_snapshot_body(r: &mut R, out: &mut Snapshot) -> Result<(), DecodeError> {
    let pcount = r.u8()?;
    if pcount as usize > MAX_PLAYERS {
        return Err(DecodeError::OutOfRange("player count > MaxPlayers"));
    }
    for _ in 0..pcount {
        out.players.push(read_player_snap_full(r)?);
    }

    let ecount = r.u16()?;
    let etotal = r.u32()?;
    out.enemy_total_in_world = etotal;
    for _ in 0..ecount {
        out.enemies.push(read_enemy_snap_full(r)?);
    }

    let fcount = r.u8()?;
    if fcount > 16 {
        return Err(DecodeError::OutOfRange("fire event count > 16"));
    }
    for _ in 0..fcount {
        out.recent_fire_events.push(read_fire_event(r)?);
    }
    Ok(())
}

// ---------- Snapshot: delta body ----------

pub fn write_delta_snapshot_body(
    buf: &mut Vec<u8>,
    players: &[PlayerSnap],
    changed: &[EnemyDeltaEntry],
    removed: &[u16],
    added: &[EnemySnap],
    enemy_total_in_world: u32,
    fires: &[FireEvent],
) {
    let mut w = W::new(buf);
    let pcount = players.len().min(u8::MAX as usize);
    w.u8(pcount as u8);
    for p in &players[..pcount] {
        write_player_snap_full(&mut w, p);
    }

    let ccount = changed.len().min(u16::MAX as usize);
    w.u16(ccount as u16);
    for c in &changed[..ccount] {
        w.u16(c.id);
        w.i16(quant_pos(c.pos_x));
        w.i16(quant_pos(c.pos_y));
    }

    let rcount = removed.len().min(u16::MAX as usize);
    w.u16(rcount as u16);
    for id in &removed[..rcount] {
        w.u16(*id);
    }

    let acount = added.len().min(u16::MAX as usize);
    w.u16(acount as u16);
    for a in &added[..acount] {
        write_enemy_snap_full(&mut w, a);
    }

    w.u32(enemy_total_in_world);

    let fcount = fires.len().min(16);
    w.u8(fcount as u8);
    for f in &fires[..fcount] {
        write_fire_event(&mut w, f);
    }
}

pub fn apply_delta_snapshot_body(
    r: &mut R,
    baseline: &Snapshot,
    out: &mut Snapshot,
) -> Result<(), DecodeError> {
    let pcount = r.u8()?;
    if pcount as usize > MAX_PLAYERS {
        return Err(DecodeError::OutOfRange("player count > MaxPlayers"));
    }
    for _ in 0..pcount {
        out.players.push(read_player_snap_full(r)?);
    }

    // Enemies: start from baseline and mutate.
    out.enemies.extend_from_slice(&baseline.enemies);

    let changed_count = r.u16()?;
    for _ in 0..changed_count {
        let id = r.u16()?;
        let px = r.i16()?;
        let py = r.i16()?;
        let pos_x = dequant_pos(px);
        let pos_y = dequant_pos(py);
        if let Some(e) = out.enemies.iter_mut().find(|e| e.id == id) {
            e.pos_x = pos_x;
            e.pos_y = pos_y;
        }
        // Else: id not in baseline — drop silently, fixed-size entry consumed.
    }

    let removed_count = r.u16()?;
    for _ in 0..removed_count {
        let id = r.u16()?;
        if let Some(idx) = out.enemies.iter().position(|e| e.id == id) {
            out.enemies.remove(idx); // stable remove, matches C# List.RemoveAt
        }
    }

    let new_count = r.u16()?;
    for _ in 0..new_count {
        out.enemies.push(read_enemy_snap_full(r)?);
    }

    out.enemy_total_in_world = r.u32()?;

    let fcount = r.u8()?;
    if fcount > 16 {
        return Err(DecodeError::OutOfRange("fire event count > 16"));
    }
    for _ in 0..fcount {
        out.recent_fire_events.push(read_fire_event(r)?);
    }
    Ok(())
}

// ---------- Snapshot: top-level (header + body) ----------

/// Decodes a snapshot into `out`. For Delta snapshots, `baseline` must be the
/// previously decoded snapshot whose `tick == header.baseline_tick`.
/// Returns Ok(false) iff the body is Delta but the baseline is missing or
/// has the wrong tick — caller should request a Full re-sync.
pub fn read_snapshot_message(
    r: &mut R,
    baseline: Option<&Snapshot>,
    out: &mut Snapshot,
) -> Result<bool, DecodeError> {
    let h = read_snapshot_header(r)?;
    out.clear();
    out.kind = h.kind;
    out.tick = h.tick;
    out.baseline_tick = h.baseline_tick;
    out.last_processed_input_tick = h.last_processed_input_tick;
    out.round = h.round;
    out.round_timer = h.round_timer;
    out.inter_round_timer = h.inter_round_timer;
    out.phase = h.phase;

    if h.kind == SnapshotKind::Full {
        read_full_snapshot_body(r, out)?;
        return Ok(true);
    }

    let baseline = match baseline {
        Some(b) if b.tick == h.baseline_tick => b,
        _ => return Ok(false),
    };
    apply_delta_snapshot_body(r, baseline, out)?;
    Ok(true)
}

/// Stateful decoder that retains the last successfully decoded snapshot as
/// the baseline for the next call. Mirror of WireCodec.cs's `SnapshotDecoder`.
pub struct SnapshotDecoder {
    baseline: Snapshot,
    have_baseline: bool,
}

impl SnapshotDecoder {
    pub fn new() -> Self {
        Self {
            baseline: Snapshot::with_capacity(),
            have_baseline: false,
        }
    }

    pub fn decode(&mut self, r: &mut R, out: &mut Snapshot) -> Result<bool, DecodeError> {
        let baseline = if self.have_baseline {
            Some(&self.baseline)
        } else {
            None
        };
        if !read_snapshot_message(r, baseline, out)? {
            return Ok(false);
        }
        self.baseline.copy_from(out);
        self.have_baseline = true;
        Ok(true)
    }

    pub fn reset(&mut self) {
        self.baseline.clear();
        self.have_baseline = false;
    }
}

impl Default for SnapshotDecoder {
    fn default() -> Self {
        Self::new()
    }
}

// ---------- Tests ----------

#[cfg(test)]
mod tests {
    use super::*;

    fn approx(a: f32, b: f32, eps: f32) -> bool {
        (a - b).abs() < eps
    }

    #[test]
    fn client_hello_roundtrip() {
        let mut buf = Vec::new();
        write_client_hello(&mut buf, 0);
        let mut r = R::new(&buf);
        let (v, c) = read_client_hello(&mut r).unwrap();
        assert_eq!(v, PROTOCOL_VERSION);
        assert_eq!(c, 0);
        assert_eq!(r.remaining(), 0);
    }

    #[test]
    fn server_welcome_roundtrip() {
        let mut buf = Vec::new();
        write_server_welcome(&mut buf, 2, 123_456);
        let mut r = R::new(&buf);
        let sw = read_server_welcome(&mut r).unwrap();
        assert_eq!(sw.version, PROTOCOL_VERSION);
        assert_eq!(sw.player_id, 2);
        assert_eq!(sw.server_tick, 123_456);
        assert_eq!(sw.sim_tick_ms, SIM_TICK_MS as u16);
        assert_eq!(sw.snapshot_every_n_ticks, SNAPSHOT_EVERY_N_TICKS as u16);
        assert_eq!(r.remaining(), 0);
    }

    #[test]
    fn input_batch_roundtrip_and_size() {
        let cmds = vec![
            InputCmd {
                tick: 100,
                last_acked_server_tick: 50,
                move_x: 0.5,
                move_y: -0.25,
                aim_x: 0.6,
                aim_y: 0.8,
                flags: InputFlags::FIRE,
            },
            InputCmd {
                tick: 99,
                last_acked_server_tick: 50,
                move_x: 0.0,
                move_y: 0.0,
                aim_x: 1.0,
                aim_y: 0.0,
                flags: InputFlags::NONE,
            },
        ];
        let mut buf = Vec::new();
        write_input_batch(&mut buf, &cmds);
        assert_eq!(buf.len(), 1 + 2 * INPUT_CMD_WIRE_BYTES);

        let mut out = Vec::new();
        let mut r = R::new(&buf);
        let n = read_input_batch(&mut r, &mut out).unwrap();
        assert_eq!(n, 2);
        assert_eq!(out[0].tick, 100);
        assert!(out[0].fire());
        assert_eq!(out[1].tick, 99);
        assert!(!out[1].fire());
        assert_eq!(r.remaining(), 0);
    }

    #[test]
    fn full_snapshot_roundtrip_with_quantization() {
        let players = vec![PlayerSnap {
            id: 0,
            pos_x: 1.5,
            pos_y: -2.0,
            aim_x: 0.0,
            aim_y: 1.0,
            alive: true,
            disable_timer: 0.0,
        }];
        let enemies = vec![
            EnemySnap {
                id: 1,
                pos_x: 5.0,
                pos_y: 5.0,
            },
            EnemySnap {
                id: 2,
                pos_x: -3.5,
                pos_y: 2.25,
            },
        ];
        // NOTE: distance is i16 mm, so the wire caps at ±32.767 m even though
        // BulletMaxRange is 50 m. Using 12.5 m here keeps the round-trip exact;
        // a 50 m fire would clamp to ~32.767 on both Rust and C# sides.
        let fires = vec![FireEvent {
            tick: 100,
            shooter_id: 0,
            origin_x: 1.5,
            origin_y: -2.0,
            dir_x: 0.0,
            dir_y: 1.0,
            distance: 12.5,
            kill_count: 3,
        }];

        let mut s = Snapshot::default();
        s.kind = SnapshotKind::Full;
        s.tick = 100;
        s.last_processed_input_tick = 99;
        s.round = 1;
        s.round_timer = 2.5;
        s.phase = RoundPhase::InRound;

        let mut buf = Vec::new();
        write_snapshot_header(&mut buf, &s);
        write_full_snapshot_body(&mut buf, &players, &enemies, 2, &fires);

        assert_eq!(
            buf.len(),
            SNAPSHOT_HEADER_BYTES
                + 1
                + 1 * PLAYER_SNAP_FULL_BYTES
                + 2
                + 4
                + 2 * ENEMY_SNAP_FULL_BYTES
                + 1
                + 1 * FIRE_EVENT_BYTES
        );

        let mut r = R::new(&buf);
        let mut out = Snapshot::default();
        assert!(read_snapshot_message(&mut r, None, &mut out).unwrap());
        assert_eq!(r.remaining(), 0);

        assert_eq!(out.kind, SnapshotKind::Full);
        assert_eq!(out.tick, 100);
        assert_eq!(out.phase, RoundPhase::InRound);
        assert_eq!(out.players.len(), 1);
        assert_eq!(out.enemies.len(), 2);
        assert_eq!(out.enemy_total_in_world, 2);
        assert_eq!(out.recent_fire_events.len(), 1);
        assert!(approx(out.players[0].pos_x, 1.5, 0.001));
        assert!(approx(out.players[0].pos_y, -2.0, 0.001));
        // aim quantization round-trip
        assert!(approx(out.players[0].aim_x, 0.0, 0.001));
        assert!(approx(out.players[0].aim_y, 1.0, 0.001));
        assert!(approx(out.enemies[0].pos_x, 5.0, 0.001));
        assert!(approx(out.recent_fire_events[0].distance, 12.5, 0.001));
    }

    #[test]
    fn delta_snapshot_roundtrip() {
        let mut baseline = Snapshot::default();
        baseline.tick = 50;
        baseline.enemies = vec![
            EnemySnap {
                id: 1,
                pos_x: 5.0,
                pos_y: 5.0,
            },
            EnemySnap {
                id: 2,
                pos_x: -3.0,
                pos_y: 2.0,
            },
            EnemySnap {
                id: 3,
                pos_x: 0.0,
                pos_y: 0.0,
            },
        ];

        let players = vec![PlayerSnap {
            id: 0,
            pos_x: 1.0,
            pos_y: 0.0,
            aim_x: 1.0,
            aim_y: 0.0,
            alive: true,
            disable_timer: 0.0,
        }];
        let changed = vec![EnemyDeltaEntry {
            id: 1,
            pos_x: 6.0,
            pos_y: 5.5,
        }];
        let removed = vec![3u16];
        let added = vec![EnemySnap {
            id: 4,
            pos_x: 10.0,
            pos_y: 10.0,
        }];
        let fires: Vec<FireEvent> = vec![];

        let mut s = Snapshot::default();
        s.kind = SnapshotKind::Delta;
        s.tick = 51;
        s.baseline_tick = 50;
        s.phase = RoundPhase::InRound;

        let mut buf = Vec::new();
        write_snapshot_header(&mut buf, &s);
        write_delta_snapshot_body(&mut buf, &players, &changed, &removed, &added, 3, &fires);

        let mut r = R::new(&buf);
        let mut out = Snapshot::default();
        assert!(read_snapshot_message(&mut r, Some(&baseline), &mut out).unwrap());
        assert_eq!(r.remaining(), 0);

        assert_eq!(out.kind, SnapshotKind::Delta);
        assert_eq!(out.baseline_tick, 50);
        assert_eq!(out.enemies.len(), 3); // 3 - 1 removed + 1 added
        let e1 = out.enemies.iter().find(|e| e.id == 1).unwrap();
        assert!(approx(e1.pos_x, 6.0, 0.001));
        assert!(approx(e1.pos_y, 5.5, 0.001));
        assert!(out.enemies.iter().all(|e| e.id != 3));
        assert!(out.enemies.iter().any(|e| e.id == 4));
    }

    #[test]
    fn delta_without_baseline_returns_false() {
        let mut s = Snapshot::default();
        s.kind = SnapshotKind::Delta;
        s.tick = 51;
        s.baseline_tick = 50;
        let mut buf = Vec::new();
        write_snapshot_header(&mut buf, &s);
        write_delta_snapshot_body(&mut buf, &[], &[], &[], &[], 0, &[]);

        let mut r = R::new(&buf);
        let mut out = Snapshot::default();
        // No baseline → false (caller should request resync).
        assert!(!read_snapshot_message(&mut r, None, &mut out).unwrap());
    }

    #[test]
    fn snapshot_decoder_tracks_baseline() {
        let mut dec = SnapshotDecoder::new();

        // First: send a Full at tick 10.
        let mut full = Snapshot::default();
        full.kind = SnapshotKind::Full;
        full.tick = 10;
        let players = vec![];
        let enemies = vec![EnemySnap {
            id: 1,
            pos_x: 0.0,
            pos_y: 0.0,
        }];
        let mut buf = Vec::new();
        write_snapshot_header(&mut buf, &full);
        write_full_snapshot_body(&mut buf, &players, &enemies, 1, &[]);

        let mut out = Snapshot::default();
        assert!(dec.decode(&mut R::new(&buf), &mut out).unwrap());
        assert_eq!(out.tick, 10);

        // Second: Delta at tick 11 against baseline 10. Decoder should
        // automatically use the previously cached Full as baseline.
        let mut delta = Snapshot::default();
        delta.kind = SnapshotKind::Delta;
        delta.tick = 11;
        delta.baseline_tick = 10;
        let changed = vec![EnemyDeltaEntry {
            id: 1,
            pos_x: 1.5,
            pos_y: 0.0,
        }];
        let mut buf2 = Vec::new();
        write_snapshot_header(&mut buf2, &delta);
        write_delta_snapshot_body(&mut buf2, &[], &changed, &[], &[], 1, &[]);

        assert!(dec.decode(&mut R::new(&buf2), &mut out).unwrap());
        assert_eq!(out.tick, 11);
        assert_eq!(out.enemies.len(), 1);
        assert!(approx(out.enemies[0].pos_x, 1.5, 0.001));
    }

    #[test]
    fn truncated_input_returns_error() {
        // 1 (count) + partial cmd
        let buf = vec![1u8, 0, 0, 0, 0];
        let mut r = R::new(&buf);
        let mut out = Vec::new();
        assert_eq!(read_input_batch(&mut r, &mut out), Err(DecodeError::Truncated));
    }

    #[test]
    fn bad_phase_byte_returns_error() {
        let mut s = Snapshot::default();
        s.phase = RoundPhase::PreGame;
        let mut buf = Vec::new();
        write_snapshot_header(&mut buf, &s);
        // Corrupt the phase byte (last byte of header) to an unknown value.
        let phase_idx = SNAPSHOT_HEADER_BYTES - 1;
        buf[phase_idx] = 99;
        let mut r = R::new(&buf);
        match read_snapshot_header(&mut r) {
            Err(DecodeError::BadEnum("RoundPhase", 99)) => {}
            other => panic!("expected BadEnum, got {:?}", other),
        }
    }

    #[test]
    fn enemy_position_change_respects_quantization() {
        let a = EnemySnap {
            id: 1,
            pos_x: 1.0000,
            pos_y: 1.0000,
        };
        // Same after quantization (sub-mm noise).
        let b = EnemySnap {
            id: 1,
            pos_x: 1.0001,
            pos_y: 1.0001,
        };
        assert!(!enemy_position_changed(&a, &b));
        // Different past quantization threshold.
        let c = EnemySnap {
            id: 1,
            pos_x: 1.002,
            pos_y: 1.000,
        };
        assert!(enemy_position_changed(&a, &c));
    }
}