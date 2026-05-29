// Mirror of DGSvsHS/Assets/_Game/Gameplay/Constants.cs.
// Any change here MUST be mirrored on the C# side and vice versa.

#![allow(dead_code)]

// ---------- Simulation ----------
pub const SIM_TICK_MS: u32 = 16;
pub const SIM_DT: f32 = SIM_TICK_MS as f32 / 1000.0;
pub const TICKS_PER_SECOND: f32 = 1000.0 / SIM_TICK_MS as f32;

// ---------- Networking ----------
pub const SNAPSHOT_EVERY_N_TICKS: u32 = 1;
pub const INPUT_RATE: u32 = TICKS_PER_SECOND as u32;
pub const INTERPOLATION_BUFFER_MS: f32 = 100.0;
pub const SNAPSHOT_HISTORY_TICKS: usize = 64;
pub const MAX_DELTA_DEPTH: u32 = 32;
pub const INPUT_HISTORY_TICKS: usize = 128;

pub const SNAPSHOT_BYTE_BUDGET: usize = 1200;

// ---------- Wire quantization ----------
pub const POSITION_SCALE: i32 = 1000;
pub const ANGLE_SCALE: i32 = 10430; // ≈ 32768/π

// ---------- Priority / staleness ----------
pub const STALENESS_WEIGHT: f32 = 0.5;
pub const MAX_SPAWNS_PER_SNAPSHOT: usize = 30;

// ---------- Client-side enemy correction ----------
pub const ENEMY_CORRECTION_K: f32 = 400.0;
pub const ENEMY_CORRECTION_C: f32 = 40.0;
pub const ENEMY_CORRECTION_SNAP_DISTANCE: f32 = 10.0;
pub const ENEMY_CORRECTION_K_MAX_MULTIPLIER: f32 = 25.0;
pub const ENEMY_CORRECTION_BUFFER_LATENCY_MS: f32 = 50.0;
pub const ENEMY_CORRECTION_BUFFER_CAPACITY: usize = 8;

// ---------- Arena ----------
pub const ARENA_RADIUS: f32 = 25.0;

// ---------- Player ----------
pub const PLAYER_SPEED: f32 = 6.0;
pub const PLAYER_RADIUS: f32 = 0.4;
pub const PLAYER_FIRE_COOLDOWN_SEC: f32 = 0.12;
pub const PLAYER_KILL_RADIUS: f32 = 0.5;
pub const DISABLE_DURATION_SEC: f32 = 10.0;
pub const MAX_PLAYERS: usize = 4;

// ---------- Laser ----------
pub const BULLET_MAX_RANGE: f32 = 50.0;
pub const BEAM_RADIUS: f32 = 0.2;

// ---------- Enemy ----------
pub const ENEMY_SPEED: f32 = 2.5;
pub const ENEMY_RADIUS: f32 = 0.35;
pub const ENEMY_MAX_HP: u32 = 1;
pub const MAX_ENEMIES: usize = 15000;

// ---------- Rounds ----------
pub const TOTAL_ROUNDS: u32 = 10;
pub const INTER_ROUND_DELAY_SEC: f32 = 3.0;
pub const BASE_ENEMIES_PER_ROUND: u32 = 700;
pub const ENEMY_SCALING_PER_ROUND: f32 = 1.4;
pub const ROUND_SPAWN_WINDOW_SEC: f32 = 18.0;

// ---------- Spatial grid ----------
pub const GRID_CELL_SIZE: f32 = 1.0;
pub const GRID_HALF_CELLS: i32 = 28;