// Mirror of:
//   DGSvsHS/Assets/_Game/Gameplay/{InputCmd,Snapshot,SimState}.cs
// Bit-identical wire semantics are enforced by the codec in network/codec.rs.

#![allow(dead_code)]

use super::constants::MAX_PLAYERS;

#[repr(u8)]
#[derive(Copy, Clone, Eq, PartialEq, Debug, Default)]
pub enum RoundPhase {
    #[default]
    PreGame = 0,
    InRound = 1,
    InterRound = 2,
    Victory = 3,
    Defeat = 4,
}

#[repr(u8)]
#[derive(Copy, Clone, Eq, PartialEq, Debug, Default)]
pub enum SnapshotKind {
    #[default]
    Full = 0,
    Delta = 1,
}

#[derive(Copy, Clone, Eq, PartialEq, Debug, Default)]
pub struct InputFlags(pub u8);

impl InputFlags {
    pub const NONE: Self = Self(0);
    pub const FIRE: Self = Self(1 << 0);

    pub fn fire(self) -> bool {
        self.0 & Self::FIRE.0 != 0
    }
}

#[derive(Copy, Clone, Debug, Default)]
pub struct InputCmd {
    pub tick: u32,
    pub last_acked_server_tick: u32,
    pub move_x: f32,
    pub move_y: f32,
    pub aim_x: f32,
    pub aim_y: f32,
    pub flags: InputFlags,
}

impl InputCmd {
    pub fn empty(tick: u32) -> Self {
        Self {
            tick,
            last_acked_server_tick: 0,
            move_x: 0.0,
            move_y: 0.0,
            aim_x: 1.0,
            aim_y: 0.0,
            flags: InputFlags::NONE,
        }
    }

    pub fn fire(&self) -> bool {
        self.flags.fire()
    }
}

#[derive(Copy, Clone, Debug, Default)]
pub struct PlayerState {
    pub id: u8,
    pub pos_x: f32,
    pub pos_y: f32,
    pub aim_x: f32,
    pub aim_y: f32,
    pub fire_cooldown: f32,
    pub disable_timer: f32,
    pub alive: bool,
}

impl PlayerState {
    pub fn is_disabled(&self) -> bool {
        self.disable_timer > 0.0
    }

    pub fn spawn(id: u8, pos_x: f32, pos_y: f32) -> Self {
        Self {
            id,
            pos_x,
            pos_y,
            aim_x: 1.0,
            aim_y: 0.0,
            fire_cooldown: 0.0,
            disable_timer: 0.0,
            alive: true,
        }
    }
}

#[derive(Copy, Clone, Debug, Default)]
pub struct PlayerSnap {
    pub id: u8,
    pub pos_x: f32,
    pub pos_y: f32,
    pub aim_x: f32,
    pub aim_y: f32,
    pub alive: bool,
    pub disable_timer: f32,
}

#[derive(Copy, Clone, Debug, Default)]
pub struct EnemySnap {
    pub id: u16,
    pub pos_x: f32,
    pub pos_y: f32,
}

#[derive(Copy, Clone, Debug, Default)]
pub struct EnemyDeltaEntry {
    pub id: u16,
    pub pos_x: f32,
    pub pos_y: f32,
}

#[derive(Copy, Clone, Debug, Default)]
pub struct FireEvent {
    pub tick: u32,
    pub shooter_id: u8,
    pub origin_x: f32,
    pub origin_y: f32,
    pub dir_x: f32,
    pub dir_y: f32,
    pub distance: f32,
    pub kill_count: u8,
}

#[derive(Clone, Debug, Default)]
pub struct Snapshot {
    pub kind: SnapshotKind,
    pub tick: u32,
    pub baseline_tick: u32,
    pub last_processed_input_tick: u32,
    pub round: u16,
    pub round_timer: f32,
    pub inter_round_timer: f32,
    pub phase: RoundPhase,
    pub enemy_total_in_world: u32,
    pub players: Vec<PlayerSnap>,
    pub enemies: Vec<EnemySnap>,
    pub recent_fire_events: Vec<FireEvent>,
}

impl Snapshot {
    pub fn with_capacity() -> Self {
        Self {
            players: Vec::with_capacity(MAX_PLAYERS),
            enemies: Vec::with_capacity(1024),
            recent_fire_events: Vec::with_capacity(16),
            ..Default::default()
        }
    }

    pub fn clear(&mut self) {
        self.kind = SnapshotKind::Full;
        self.tick = 0;
        self.baseline_tick = 0;
        self.last_processed_input_tick = 0;
        self.round = 0;
        self.round_timer = 0.0;
        self.inter_round_timer = 0.0;
        self.phase = RoundPhase::PreGame;
        self.enemy_total_in_world = 0;
        self.players.clear();
        self.enemies.clear();
        self.recent_fire_events.clear();
    }

    pub fn copy_from(&mut self, src: &Snapshot) {
        self.clear();
        self.kind = src.kind;
        self.tick = src.tick;
        self.baseline_tick = src.baseline_tick;
        self.last_processed_input_tick = src.last_processed_input_tick;
        self.round = src.round;
        self.round_timer = src.round_timer;
        self.inter_round_timer = src.inter_round_timer;
        self.phase = src.phase;
        self.enemy_total_in_world = src.enemy_total_in_world;
        self.players.extend_from_slice(&src.players);
        self.enemies.extend_from_slice(&src.enemies);
        self.recent_fire_events
            .extend_from_slice(&src.recent_fire_events);
    }
}