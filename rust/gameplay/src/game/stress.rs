// Stress-test harness: spawn N static enemies as bare ECS entities (no Avian,
// no physics solver) and let Bevy's diagnostics report the steady-state tick
// rate. Per-entity payload is just `Enemy` (ZST) + `Pos2D` (8 B).

use bevy::ecs::entity::Entity;
use bevy::log::info;
use bevy::prelude::*;

use super::constants::{
    ARENA_RADIUS, BEAM_RADIUS, BULLET_MAX_RANGE, ENEMY_RADIUS, ENEMY_SPEED, SIM_DT,
};
use super::spatial::{beam_hits, rebuild_grid, BeamHit, EnemyGrid, Pos2D, SpatialPlugin, Vel2D};

// Dial this to scale the stress. 100k is the target in the paper.
const ENEMY_COUNT: usize = 100_000;

#[derive(Component)]
pub struct Enemy;

pub struct StressPlugin;

impl Plugin for StressPlugin {
    fn build(&self, app: &mut App) {
        app.add_plugins(SpatialPlugin)
            .add_systems(Startup, spawn_enemies)
            // integrate → rebuild_grid → stress_beam each tick:
            //   1. positions move
            //   2. grid is rebuilt from the new positions
            //   3. a piercing beam sweeps a full revolution every 5 s, fired
            //      from the arena origin against the freshly-built grid
            .add_systems(
                Update,
                (integrate_enemies, rebuild_grid, stress_beam).chain(),
            );
    }
}

fn spawn_enemies(mut commands: Commands) {
    let n = ENEMY_COUNT;
    let r_arena = ARENA_RADIUS;
    info!(
        "[stress] spawning {} enemies in {} m arena, speed {} m/s",
        n, r_arena, ENEMY_SPEED
    );

    // Sunflower / golden-angle distribution: deterministic, uniform density.
    // Initial velocity is tangential (perpendicular to radial) so the seed
    // pattern stays uniform — no immediate pile-up at the origin like a
    // pure seek-the-center test would produce.
    let golden_angle = std::f32::consts::PI * (3.0 - (5.0_f32).sqrt());

    commands.spawn_batch((0..n).map(move |i| {
        let r = r_arena * (i as f32 / n as f32).sqrt();
        let theta = i as f32 * golden_angle;
        let cos_t = theta.cos();
        let sin_t = theta.sin();
        (
            Enemy,
            Pos2D {
                x: r * cos_t,
                y: r * sin_t,
            },
            Vel2D {
                x: -sin_t * ENEMY_SPEED,
                y: cos_t * ENEMY_SPEED,
            },
        )
    }));

    info!("[stress] spawn_batch queued");
}

// Integrate positions by velocity each tick. Reflect off the arena edge so
// entities stay inside the grid for sustained stress.
fn integrate_enemies(mut q: Query<(&mut Pos2D, &mut Vel2D), With<Enemy>>) {
    for (mut pos, mut vel) in q.iter_mut() {
        pos.x += vel.x * SIM_DT;
        pos.y += vel.y * SIM_DT;
        if pos.x.abs() > ARENA_RADIUS && pos.x * vel.x > 0.0 {
            vel.x = -vel.x;
        }
        if pos.y.abs() > ARENA_RADIUS && pos.y * vel.y > 0.0 {
            vel.y = -vel.y;
        }
    }
}

#[derive(Default)]
struct BeamState {
    angle: f32,
    cells: Vec<Entity>,
    hits: Vec<BeamHit>,
    tick: u64,
    total_hits: u64,
    sum_candidates: u64,
    last_hit_count: usize,
}

// 360° per 5 s sweep, fires once per tick.
const BEAM_SWEEP_RATE: f32 = std::f32::consts::TAU / 5.0;
const BEAM_LOG_EVERY_N_TICKS: u64 = 300; // ≈ every 5 s at 60 Hz

fn stress_beam(
    grid: Res<EnemyGrid>,
    pos_q: Query<&Pos2D>,
    mut state: Local<BeamState>,
) {
    // Reborrow through Local<>'s Deref so the disjoint field borrows of
    // state.cells + state.hits resolve cleanly inside beam_hits().
    let state: &mut BeamState = &mut state;
    state.angle = (state.angle + BEAM_SWEEP_RATE * SIM_DT) % std::f32::consts::TAU;
    let dir_x = state.angle.cos();
    let dir_y = state.angle.sin();
    // Per WireFormat.md §7.4: hit radius = enemy radius + beam radius.
    let hit_radius = ENEMY_RADIUS + BEAM_RADIUS;

    beam_hits(
        &grid,
        |e| pos_q.get(e).ok().copied(),
        0.0,
        0.0,
        dir_x,
        dir_y,
        BULLET_MAX_RANGE,
        hit_radius,
        &mut state.cells,
        &mut state.hits,
    );

    state.last_hit_count = state.hits.len();
    state.total_hits += state.hits.len() as u64;
    state.sum_candidates += state.cells.len() as u64;
    state.tick += 1;

    if state.tick % BEAM_LOG_EVERY_N_TICKS == 0 {
        info!(
            "[beam] tick={} avg_hits/tick={:.1} avg_candidates/tick={:.0} last_hits={}",
            state.tick,
            state.total_hits as f32 / state.tick as f32,
            state.sum_candidates as f32 / state.tick as f32,
            state.last_hit_count,
        );
    }
}