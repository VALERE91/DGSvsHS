// Lag-compensated hit resolution. Ports of the DOTS RewindRecordSystem and
// RewindResolveSystem and the WireFormat.md §7 rewind contract.
//
// The ring stores one frame of (enemy_id, position) per server tick, captured
// at the END of the tick (after integrate). On a Fire command, the resolver
// rewinds to a fractional view-time, builds a bracketing-frame-interpolated
// enemy set, runs the piercing beam against it, and applies kills to the
// CURRENT world by id.
//
// `rewind_resolve` is an exclusive system so kills despawn immediately (before
// seek/integrate/contact run this tick), matching the DOTS ECB playback inside
// RewindResolveSystem.OnUpdate.

use std::collections::HashMap;

use bevy::prelude::*;

use super::components::*;
use crate::game::constants::*;
use crate::game::spatial::{GridGeom, Pos2D};
use crate::game::types::FireEvent;

// ---------- Ring ----------

/// One tick of enemy history, spatially bucketed so `resolve_fire` only scans
/// cells along the beam instead of every enemy. `cells` holds (id, pos) by
/// `GridGeom` cell; `by_id` is the O(1) cross-frame lookup the bracketing
/// lerp + floor/ceil membership tests need. Enemies are arena-clamped inside
/// the grid extent, so none fall out of bounds.
pub struct RewindFrame {
    pub tick: u32,
    cells: Vec<Vec<(u32, Vec2)>>,
    by_id: HashMap<u32, Vec2>,
}

impl RewindFrame {
    fn new(geom: &GridGeom) -> Self {
        Self {
            tick: 0,
            cells: (0..geom.cell_count()).map(|_| Vec::new()).collect(),
            by_id: HashMap::new(),
        }
    }
}

#[derive(Resource)]
pub struct RewindRing {
    frames: Vec<RewindFrame>,
    geom: GridGeom,
    head: usize,
    count: usize,
    cap: usize,
}

impl RewindRing {
    pub fn new(cap: usize) -> Self {
        let geom = GridGeom::from_constants();
        let frames = (0..cap).map(|_| RewindFrame::new(&geom)).collect();
        Self {
            frames,
            geom,
            head: 0,
            count: 0,
            cap,
        }
    }

    /// Overwrite the head slot with this tick's enemy positions and advance.
    /// Reuses the slot's per-cell Vecs + map allocation to avoid per-tick churn.
    pub fn record(&mut self, tick: u32, it: impl Iterator<Item = (u32, Vec2)>) {
        crate::hot_span!("rewind_ring_record");
        let geom = self.geom;
        let slot = &mut self.frames[self.head];
        slot.tick = tick;
        for cell in &mut slot.cells {
            cell.clear();
        }
        slot.by_id.clear();
        for (id, pos) in it {
            slot.by_id.insert(id, pos);
            if let Some((cx, cy)) = geom.cell_coords(pos.x, pos.y) {
                slot.cells[geom.cell_idx(cx, cy)].push((id, pos));
            }
        }
        self.head = (self.head + 1) % self.cap;
        if self.count < self.cap {
            self.count += 1;
        }
    }

    pub fn clear(&mut self) {
        self.head = 0;
        self.count = 0;
    }

    pub fn count(&self) -> usize {
        self.count
    }

    fn oldest_index(&self) -> usize {
        (self.head + self.cap - self.count) % self.cap
    }

    /// Bracketing slots for a fractional view-tick. Returns (floor_idx,
    /// ceil_idx, alpha). Mirrors DOTS FindBracketingSlots, including the
    /// clamp-to-oldest fallback when the view precedes the buffer.
    fn bracket(&self, view_tick_f: f32) -> Option<(usize, usize, f32)> {
        if self.count == 0 {
            return None;
        }
        // i64 avoids the negative-float → unsigned wrap hazard for very early
        // ticks / high latency; the clamp-to-oldest path is what matters there.
        let view_floor = view_tick_f.floor() as i64;
        let view_ceil = view_floor + 1;

        let mut floor_slot: Option<usize> = None;
        let mut ceil_slot: Option<usize> = None;
        for i in 0..self.count {
            let idx = (self.head + self.cap - 1 - i) % self.cap;
            let t = self.frames[idx].tick as i64;
            if t == view_floor {
                floor_slot = Some(idx);
            }
            if t == view_ceil {
                ceil_slot = Some(idx);
            }
        }

        match floor_slot {
            None => {
                let oldest = self.oldest_index();
                Some((oldest, oldest, 0.0))
            }
            Some(f) => match ceil_slot {
                None => Some((f, f, 0.0)),
                Some(c) => {
                    let ft = self.frames[f].tick as f32;
                    let alpha = (view_tick_f - ft).clamp(0.0, 1.0);
                    Some((f, c, alpha))
                }
            },
        }
    }

    /// Resolve one fire against the interpolated set. Claims every enemy id the
    /// beam hits into the shared `kill_owner` map (id → index of the FIRST beam
    /// to hit it, so an enemy hit by an earlier beam this tick isn't re-claimed
    /// — matching the DOTS shared KillFlags). Returns how many ids THIS beam
    /// newly claimed. Liveness is NOT checked here: the caller's single query
    /// pass (`tally_kills`) filters claims down to enemies still in the world,
    /// which yields the same despawns + per-beam counts as gating here would.
    pub fn resolve_fire(
        &self,
        fire: &PendingFire,
        view_tick_f: f32,
        fire_index: usize,
        kill_owner: &mut HashMap<u32, usize>,
    ) -> u32 {
        crate::hot_span!("rewind_resolve_fire");
        let Some((floor_i, ceil_i, alpha)) = self.bracket(view_tick_f) else {
            return 0;
        };
        let floor = &self.frames[floor_i];
        let ceil = &self.frames[ceil_i];
        let same = floor_i == ceil_i;
        let hit_r = ENEMY_RADIUS + BEAM_RADIUS;
        let hit_r_sq = hit_r * hit_r;

        // Candidate cells: the beam AABB expanded by the hit radius PLUS one
        // cell. The extra cell covers the bracketing lerp — an enemy is bucketed
        // by its floor/ceil position but hit-tested at the lerped position, and
        // inter-frame movement (one tick, ≪ cell size) can nudge it across a
        // cell boundary. The narrow-phase `segment_hits` still filters exactly,
        // so the wider scan only adds candidates, never changes the result.
        let end = fire.origin + fire.dir * BULLET_MAX_RANGE;
        let query_r = hit_r + self.geom.cell_size;
        let Some((cxmin, cymin, cxmax, cymax)) =
            self.geom
                .segment_cell_range(fire.origin.x, fire.origin.y, end.x, end.y, query_r)
        else {
            return 0;
        };

        let mut local = 0u32;

        // Floor-frame enemies; lerp with ceil if the same id is present there.
        // Floor-only enemies (died mid-bracket) stay at floor.pos.
        for cy in cymin..=cymax {
            for cx in cxmin..=cxmax {
                let cell = &floor.cells[self.geom.cell_idx(cx, cy)];
                for (id, fpos) in cell {
                    let mut pos = *fpos;
                    if !same {
                        if let Some(cpos) = ceil.by_id.get(id) {
                            pos = fpos.lerp(*cpos, alpha);
                        }
                    }
                    if segment_hits(fire.origin, fire.dir, BULLET_MAX_RANGE, pos, hit_r_sq)
                        && claim(kill_owner, *id, fire_index)
                    {
                        local += 1;
                    }
                }
            }
        }

        // Ceil-only enemies (spawned mid-bracket) included only if alpha ≥ 0.5.
        if alpha >= 0.5 && !same {
            for cy in cymin..=cymax {
                for cx in cxmin..=cxmax {
                    let cell = &ceil.cells[self.geom.cell_idx(cx, cy)];
                    for (id, cpos) in cell {
                        if floor.by_id.contains_key(id) {
                            continue;
                        }
                        if segment_hits(fire.origin, fire.dir, BULLET_MAX_RANGE, *cpos, hit_r_sq)
                            && claim(kill_owner, *id, fire_index)
                        {
                            local += 1;
                        }
                    }
                }
            }
        }

        local
    }
}

/// `server_tick - (one_way_ms/1000)·TPS - (InterpBufferMs/1000)·TPS`.
pub fn compute_view_tick_f(current_tick: u32, one_way_ms: f32) -> f32 {
    current_tick as f32
        - (one_way_ms / 1000.0) * TICKS_PER_SECOND
        - (INTERPOLATION_BUFFER_MS / 1000.0) * TICKS_PER_SECOND
}

/// DOTS `SegmentHits`: project the enemy centre onto the beam ray, accept if the
/// projection lands within [0, max_range] and the perpendicular distance is
/// within the hit radius. (Deliberately NOT spatial.rs's entry-point test — the
/// kill decision must match the reference rewind exactly.)
fn segment_hits(origin: Vec2, dir: Vec2, max_range: f32, enemy: Vec2, hit_radius_sq: f32) -> bool {
    let to_enemy = enemy - origin;
    let t = to_enemy.dot(dir);
    if t < 0.0 || t > max_range {
        return false;
    }
    let closest = origin + dir * t;
    (enemy - closest).length_squared() <= hit_radius_sq
}

/// First-claim-wins: record that beam `fire_index` hit enemy `id`, unless an
/// earlier beam already claimed it. Returns true only on the first claim.
fn claim(kill_owner: &mut HashMap<u32, usize>, id: u32, fire_index: usize) -> bool {
    use std::collections::hash_map::Entry;
    match kill_owner.entry(id) {
        Entry::Vacant(slot) => {
            slot.insert(fire_index);
            true
        }
        Entry::Occupied(_) => false,
    }
}

/// Single pass over the live enemy set: for each enemy whose id was claimed,
/// tally the kill to its owning beam and mark its entity for despawn. Pure +
/// generic over the entity type so it's testable without a `World`. This is
/// where liveness is enforced — claimed ids with no live enemy simply produce
/// no kill and no despawn.
fn tally_kills<E>(
    kill_owner: &HashMap<u32, usize>,
    live: impl Iterator<Item = (E, u32)>,
    fire_count: usize,
) -> (Vec<u32>, Vec<E>) {
    let mut counts = vec![0u32; fire_count];
    let mut to_despawn = Vec::new();
    for (entity, id) in live {
        if let Some(&fi) = kill_owner.get(&id) {
            counts[fi] += 1;
            to_despawn.push(entity);
        }
    }
    (counts, to_despawn)
}

// ---------- Systems ----------

/// Step 4: resolve queued fires against the rewind ring; queue kill despawns.
///
/// Normal (non-exclusive) system: despawns go through `Commands`, batched at the
/// schedule's sync point rather than mutating archetypes mid-frame. The kill
/// therefore lands one tick later — accepted here (an enemy lives one extra
/// tick) in exchange for not paying a structural-change stall per beam hit.
pub fn rewind_resolve(
    mut pending_fires: ResMut<PendingFires>,
    clock: Res<WorldClock>,
    rtt: Res<PlayerRtt>,
    ring: Res<RewindRing>,
    mut fire_events: ResMut<FireEvents>,
    mut commands: Commands,
    enemies: Query<(Entity, &EnemyId), With<Enemy>>,
) {
    let pending = std::mem::take(&mut pending_fires.0);
    if pending.is_empty() {
        return;
    }
    let current_tick = clock.0;

    // Beam pass: claim every hit id into a small map (id → first beam to hit
    // it). Size is bounded by enemies near beams, not the world total.
    let mut kill_owner: HashMap<u32, usize> = HashMap::new();
    for (fi, f) in pending.iter().enumerate() {
        let one_way = 0.5 * rtt.0.get(f.player_id as usize).copied().unwrap_or(60.0);
        let view_tick_f = compute_view_tick_f(current_tick, one_way);
        ring.resolve_fire(f, view_tick_f, fi, &mut kill_owner);
    }

    // One query pass owns id→entity + liveness: tally live claims per beam and
    // collect the entities to despawn.
    let (counts, to_despawn) =
        tally_kills(&kill_owner, enemies.iter().map(|(e, id)| (e, id.0)), pending.len());
    for e in to_despawn {
        commands.entity(e).despawn();
    }

    fire_events
        .0
        .extend(pending.iter().enumerate().map(|(fi, f)| FireEvent {
            tick: current_tick,
            shooter_id: f.player_id,
            origin_x: f.origin.x,
            origin_y: f.origin.y,
            dir_x: f.dir.x,
            dir_y: f.dir.y,
            distance: BULLET_MAX_RANGE,
            kill_count: counts[fi].min(255) as u8,
        }));
}

/// Step 8: record post-integrate enemy positions into the ring for this tick.
pub fn rewind_record(
    clock: Res<WorldClock>,
    mut ring: ResMut<RewindRing>,
    enemies: Query<(&EnemyId, &Pos2D), With<Enemy>>,
) {
    ring.record(clock.0, enemies.iter().map(|(id, p)| (id.0, pos_vec(p))));
}

// ---------- Tests ----------

#[cfg(test)]
mod tests {
    use super::*;

    fn beam_along_x() -> PendingFire {
        PendingFire {
            player_id: 0,
            client_input_tick: 0,
            origin: Vec2::ZERO,
            dir: Vec2::new(1.0, 0.0),
        }
    }

    #[test]
    fn lerps_between_bracketing_frames() {
        let mut ring = RewindRing::new(8);
        ring.record(10, [(1u32, Vec2::new(10.0, 0.0))].into_iter());
        ring.record(11, [(1u32, Vec2::new(8.0, 0.0))].into_iter());

        // view 10.5 → enemy at lerp((10,0),(8,0),0.5) = (9,0); beam on x-axis hits.
        let mut owner = HashMap::new();
        let local = ring.resolve_fire(&beam_along_x(), 10.5, 0, &mut owner);
        assert_eq!(local, 1);
        assert!(owner.contains_key(&1));
    }

    #[test]
    fn clamps_to_oldest_when_view_precedes_buffer() {
        let mut ring = RewindRing::new(8);
        ring.record(10, [(1u32, Vec2::new(10.0, 0.0))].into_iter());
        ring.record(11, [(1u32, Vec2::new(8.0, 0.0))].into_iter());

        // view 3.0 is older than the oldest frame (tick 10) → clamp to tick 10.
        let mut owner = HashMap::new();
        let local = ring.resolve_fire(&beam_along_x(), 3.0, 0, &mut owner);
        assert_eq!(local, 1);
    }

    #[test]
    fn claimed_id_with_no_live_enemy_is_not_killed_or_counted() {
        // An enemy hit in the ring but already gone from the world: it's claimed
        // but `tally_kills` finds no live entity → no kill, no despawn. This is
        // the liveness gate that used to live in `resolve_fire`.
        let owner = HashMap::from([(1u32, 0usize)]);
        let live: [(u32, u32); 0] = []; // (entity, id) — empty world
        let (counts, to_despawn) = tally_kills(&owner, live.into_iter(), 1);
        assert_eq!(counts, vec![0]);
        assert!(to_despawn.is_empty());
    }

    #[test]
    fn tally_attributes_each_kill_to_its_owning_beam() {
        // Beam 0 claimed id 1, beam 1 claimed id 2; id 3 was never claimed.
        let owner = HashMap::from([(1u32, 0usize), (2u32, 1usize)]);
        // Fake entities as u32; (entity, id) pairs of the live world.
        let live = [(10u32, 1u32), (20u32, 2u32), (30u32, 3u32)];
        let (counts, to_despawn) = tally_kills(&owner, live.into_iter(), 2);
        assert_eq!(counts, vec![1, 1]);
        assert_eq!(to_despawn, vec![10, 20]); // id 3's entity is untouched
    }

    #[test]
    fn off_axis_enemy_is_missed() {
        let mut ring = RewindRing::new(8);
        // 5 m off the beam axis — well outside enemy+beam radius.
        ring.record(10, [(1u32, Vec2::new(10.0, 5.0))].into_iter());
        let mut owner = HashMap::new();
        let local = ring.resolve_fire(&beam_along_x(), 10.0, 0, &mut owner);
        assert_eq!(local, 0);
        assert!(owner.is_empty());
    }

    #[test]
    fn enemy_in_distant_cell_is_not_hit() {
        // Enemy far from the beam (but in-grid): its cell isn't scanned, and the
        // narrow phase wouldn't hit it anyway. Guards the spatial-cull path.
        let mut ring = RewindRing::new(8);
        ring.record(
            10,
            [
                (1u32, Vec2::new(5.0, 0.0)),   // on the beam → claimed
                (2u32, Vec2::new(20.0, 20.0)), // distant corner → missed
            ]
            .into_iter(),
        );
        let mut owner = HashMap::new();
        let local = ring.resolve_fire(&beam_along_x(), 10.0, 0, &mut owner);
        assert_eq!(local, 1);
        assert!(owner.contains_key(&1) && !owner.contains_key(&2));
    }

    #[test]
    fn piercing_claims_all_in_path_once() {
        let mut ring = RewindRing::new(8);
        ring.record(
            10,
            [
                (1u32, Vec2::new(3.0, 0.0)),
                (2u32, Vec2::new(6.0, 0.0)),
                (3u32, Vec2::new(9.0, 0.0)),
                (4u32, Vec2::new(6.0, 4.0)), // off-axis decoy
            ]
            .into_iter(),
        );
        let mut owner = HashMap::new();
        let local = ring.resolve_fire(&beam_along_x(), 10.0, 0, &mut owner);
        assert_eq!(local, 3);
        assert!(owner.contains_key(&1) && owner.contains_key(&2) && owner.contains_key(&3));
        assert!(!owner.contains_key(&4));

        // A second beam over the same already-claimed enemies claims nothing new,
        // and leaves ownership with the first beam.
        let local2 = ring.resolve_fire(&beam_along_x(), 10.0, 1, &mut owner);
        assert_eq!(local2, 0);
        assert_eq!(owner[&1], 0);
    }
}