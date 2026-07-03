// Player input application + enemy AI/movement/contact. Ports of the DOTS
// systems PlayerInputSystem, EnemySeekSystem, EnemyIntegrateSystem,
// PlayerEnemyContactSystem (WireFormat.md §8.2 steps 3, 5, 6, 7).

use avian2d::prelude::*;
use bevy::prelude::*;

use super::components::*;
use crate::game::constants::*;
use crate::game::spatial::{EnemyGrid, Pos2D, Vel2D};

const AIM_EPS_SQ: f32 = 0.0001;

pub fn tick_advance(mut clock: ResMut<WorldClock>, mut fires: ResMut<FireEvents>) {
    clock.0 += 1;
    fires.0.clear();
}

pub fn player_input(
    mut inbox: ResMut<InputInbox>,
    mut pending_fires: ResMut<PendingFires>,
    mut processed: ResMut<ProcessedInputTick>,
    mut players: Query<
        (
            &PlayerSlot,
            &mut Pos2D,
            &mut Aim2D,
            &mut FireCooldown,
            &mut DisableTimer,
            &Alive,
        ),
        With<Player>,
    >,
) {
    let mut latest: [Option<crate::game::types::InputCmd>; MAX_PLAYERS] = [None; MAX_PLAYERS];
    for (slot, cmd) in inbox.0.iter() {
        let s = *slot as usize;
        if s >= MAX_PLAYERS {
            continue;
        }
        if latest[s].map_or(true, |l| cmd.tick > l.tick) {
            latest[s] = Some(*cmd);
        }
    }

    let mut snap: [Option<(Vec2, Vec2, bool)>; MAX_PLAYERS] = [None; MAX_PLAYERS];
    for (slot, pos, aim, _cd, _dt, alive) in players.iter() {
        let s = slot.0 as usize;
        if s < MAX_PLAYERS {
            snap[s] = Some((pos_vec(pos), aim.0, alive.0));
        }
    }

    for (slot, cmd) in inbox.0.iter() {
        if !cmd.fire() {
            continue;
        }
        let s = *slot as usize;
        if s >= MAX_PLAYERS {
            continue;
        }
        let Some((pos, aim, alive)) = snap[s] else {
            continue;
        };
        if !alive {
            continue;
        }
        let in_aim = Vec2::new(cmd.aim_x, cmd.aim_y);
        let dir = if in_aim.length_squared() > AIM_EPS_SQ {
            in_aim.normalize()
        } else {
            aim
        };
        pending_fires.0.push(PendingFire {
            player_id: *slot,
            client_input_tick: cmd.tick,
            origin: pos,
            dir,
        });
    }

    let max_r = ARENA_RADIUS - PLAYER_RADIUS;
    for (slot, mut pos, mut aim, mut cd, mut dt, alive) in players.iter_mut() {
        let s = slot.0 as usize;
        let cmd = if alive.0 && s < MAX_PLAYERS {
            latest[s]
        } else {
            None
        };

        if let Some(cmd) = cmd {
            // Movement — clamp magnitude so diagonals don't exceed PlayerSpeed.
            let mut mv = Vec2::new(cmd.move_x, cmd.move_y);
            let mag = mv.length();
            if mag > 1.0 {
                mv /= mag;
            }
            let mut new_pos = pos_vec(&pos) + mv * PLAYER_SPEED * SIM_DT;
            let r = new_pos.length();
            if r > max_r {
                new_pos *= max_r / r;
            }
            pos.x = new_pos.x;
            pos.y = new_pos.y;

            let in_aim = Vec2::new(cmd.aim_x, cmd.aim_y);
            if in_aim.length_squared() > AIM_EPS_SQ {
                aim.0 = in_aim.normalize();
            }
            processed.0[s] = cmd.tick;
        }

        cd.0 = (cd.0 - SIM_DT).max(0.0);
        dt.0 = (dt.0 - SIM_DT).max(0.0);
    }

    inbox.0.clear();
}

pub fn enemy_seek(
    players: Query<(&PlayerSlot, &Pos2D, &Alive, &DisableTimer), With<Player>>,
    // `Without<Sleeping>` aligns Avian's crowd behaviour with the DOTS/Unity.Physics
    // leg. In a 7x-oversubscribed jam the packed core has ~zero velocity; Unity's
    // solver deactivates those bodies (islands drop out of the solve) while ours
    // used to keep the entire jam awake by applying drive every tick. Gating the
    // drive on non-sleeping enemies lets Avian's (default-on) sleeping put the dead
    // core to sleep. The moving fringe stays awake and wakes neighbours on contact
    // as the jam shifts, so seek behaviour is preserved. Kills/disable go through
    // the hand-rolled grid, independent of physics sleep, so sleeping enemies are
    // still resolved correctly.
    mut enemies: Query<(&Pos2D, Forces), (With<Enemy>, Without<Sleeping>)>,
) {
    // Build target list in slot order so the (vanishingly rare) exact-distance
    // tie-break is deterministic across runs/builds.
    let mut targets: Vec<(u8, Vec2)> = players
        .iter()
        .filter(|(_, _, alive, dt)| alive.0 && dt.0 <= 0.0)
        .map(|(slot, p, _, _)| (slot.0, pos_vec(p)))
        .collect();
    targets.sort_by_key(|(slot, _)| *slot);
    let targets: Vec<Vec2> = targets.into_iter().map(|(_, p)| p).collect();

    if targets.is_empty() {
        return;
    }
    for (pos, mut forces) in enemies.iter_mut() {
        let p = pos_vec(pos);
        let mut best = targets[0];
        let mut best_sq = f32::MAX;
        for t in &targets {
            let sq = (*t - p).length_squared();
            if sq < best_sq {
                best_sq = sq;
                best = *t;
            }
        }
        let mut dir = best - p;
        let len = best_sq.sqrt();
        if len > 0.0001 {
            dir /= len;
        } else {
            continue;
        }
        // Continuous steering force, mirroring the DOTS/Unreal legs: Avian
        // integrates it as Δv = (F/m)·dt and LinearDamping bleeds to terminal speed
        // EnemyDriveForce / EnemyLinearDamping = EnemySpeed. The `Without<Sleeping>`
        // filter above means only awake enemies are driven, so a jammed core with
        // ~zero velocity still sleeps; contacts wake bodies back into the query when
        // the jam shifts.
        forces.apply_force(dir * ENEMY_DRIVE_FORCE);
    }
}

pub fn sync_pos2d_to_physics(
    mut players: Query<(&Pos2D, &mut Position), With<Player>>,
) {
    for (pos2d, mut position) in players.iter_mut() {
        position.0.x = pos2d.x;
        position.0.y = pos2d.y;
    }
}

pub fn sync_physics_to_pos2d(
    god: Res<GodMode>,
    mut grid: ResMut<EnemyGrid>,
    mut q: Query<(
        Entity,
        &mut Pos2D,
        &mut Vel2D,
        &mut Position,
        &mut LinearVelocity,
        Has<Player>,
    )>,
) {
    let enemy_max = ARENA_RADIUS - ENEMY_RADIUS;
    let player_max = ARENA_RADIUS - PLAYER_RADIUS;
    // Rebuild the enemy spatial grid in the SAME pass that lands final
    // positions, so player_enemy_contact (next set) can query it without a
    // second O(N) traversal. Skipped in god mode, where contact is a no-op.
    let maintain_grid = !god.0;
    if maintain_grid {
        grid.clear();
    }
    for (entity, mut pos2d, mut vel2d, mut position, mut lv, is_player) in q.iter_mut() {
        let max_r = if is_player { player_max } else { enemy_max };
        let r = position.0.length();
        if r > max_r {
            position.0 *= max_r / r;
        }
        pos2d.x = position.0.x;
        pos2d.y = position.0.y;
        vel2d.x = lv.0.x;
        vel2d.y = lv.0.y;
        // Also reset kinematic player velocity each tick (input drives position
        // directly; we don't want residual velocity bleeding into next tick).
        if is_player {
            lv.0 = Vec2::ZERO;
        } else if maintain_grid {
            grid.insert(entity, *pos2d);
        }
    }
}

pub fn player_enemy_contact(
    god: Res<GodMode>,
    grid: Res<EnemyGrid>,
    enemies: Query<&Pos2D, With<Enemy>>,
    mut players: Query<(&Pos2D, &mut DisableTimer, &Alive), With<Player>>,
    mut candidates: Local<Vec<Entity>>,
) {
    if god.0 {
        return;
    }
    let kill_r = PLAYER_KILL_RADIUS + ENEMY_RADIUS;
    let kill_r_sq = kill_r * kill_r;

    // The grid was rebuilt this tick by sync_physics_to_pos2d. Query only the
    // cells around each player, then narrow-phase the few candidates exactly.
    for (pos, mut dt, alive) in players.iter_mut() {
        if !alive.0 || dt.0 > 0.0 {
            continue;
        }
        // Degenerate segment = point query of the kill-radius neighbourhood.
        grid.collect_along_segment(pos.x, pos.y, pos.x, pos.y, kill_r, &mut candidates);
        for &cand in candidates.iter() {
            let Ok(ep) = enemies.get(cand) else {
                continue;
            };
            let dx = ep.x - pos.x;
            let dy = ep.y - pos.y;
            if dx * dx + dy * dy <= kill_r_sq {
                dt.0 = DISABLE_DURATION_SEC;
                break;
            }
        }
    }
}