// Per-recipient enemy selection. Port of csharp_arch_server/Net/SnapshotPriority.cs.
//
// SelectForFull: rank all current enemies by distance² to the recipient anchor
// and pack the closest ones until the byte budget is exhausted.
//
// SelectForDelta (3 lanes):
//   1. Removed — baseline-confirmed IDs no longer in current. Tiny (2 B each),
//      packed first. If even those overflow, truncate to half the budget.
//   2. Spawn lane — current IDs the recipient hasn't yet seen. Sorted by
//      distance², capped at MaxSpawnsPerSnapshot.
//   3. Animation lane — confirmed-and-in-baseline IDs whose quantized position
//      changed. Score = distance − StalenessWeight × ticks_since_last_sent so
//      stale-but-far enemies still get refreshed.

use std::collections::HashMap;

use bevy::math::Vec2;

use crate::game::constants::{MAX_SPAWNS_PER_SNAPSHOT, STALENESS_WEIGHT};
use crate::game::{EnemyDeltaEntry, EnemySnap, Snapshot};
use crate::network::{
    enemy_position_changed, ENEMY_DELTA_ENTRY_BYTES, ENEMY_SNAP_FULL_BYTES,
};

use super::bitset::IdBitSet;

#[derive(Copy, Clone, Debug)]
pub struct ScoredEnemy {
    pub index: usize,
    pub score: f32,
}

/// Total order over scores with `index` as the tie-break — matches the old
/// stable sort-by-score (which preserved push order, i.e. ascending index, on
/// ties). Being total lets `select_nth_unstable_by` pick a well-defined K
/// smallest identical to a full sort's first K.
fn cmp_scored(a: &ScoredEnemy, b: &ScoredEnemy) -> std::cmp::Ordering {
    a.score
        .partial_cmp(&b.score)
        .unwrap_or(std::cmp::Ordering::Equal)
        .then(a.index.cmp(&b.index))
}

/// Return the `k` smallest entries (by `cmp_scored`), in sorted order, without
/// fully sorting the rest. O(n) average partition + O(k log k) on the prefix.
fn select_smallest(scored: &mut [ScoredEnemy], k: usize) -> &[ScoredEnemy] {
    if k == 0 {
        return &[];
    }
    if k >= scored.len() {
        scored.sort_unstable_by(cmp_scored);
        scored
    } else {
        scored.select_nth_unstable_by(k, cmp_scored);
        let prefix = &mut scored[..k];
        prefix.sort_unstable_by(cmp_scored);
        prefix
    }
}

pub fn select_for_full(
    current: &Snapshot,
    recipient: Vec2,
    enemy_byte_budget: usize,
    out_selected: &mut Vec<EnemySnap>,
    scratch: &mut Vec<ScoredEnemy>,
) {
    crate::hot_span!("select_for_full");
    out_selected.clear();
    scratch.clear();

    for (i, e) in current.enemies.iter().enumerate() {
        let dx = e.pos_x - recipient.x;
        let dy = e.pos_y - recipient.y;
        scratch.push(ScoredEnemy {
            index: i,
            score: dx * dx + dy * dy,
        });
    }

    // Uniform entry size ⇒ exactly `budget / size` closest enemies fit. Grab
    // just those via quickselect instead of sorting all N.
    let k = enemy_byte_budget / ENEMY_SNAP_FULL_BYTES;
    let chosen = select_smallest(scratch, k);
    for s in chosen.iter() {
        out_selected.push(current.enemies[s.index]);
    }
}

/// `current_ids` is the set of current enemy ids, and `current_index_by_id` the
/// matching `id → current-enemy index` table (`-1` = absent) — both built ONCE
/// per tick by the caller (recipient-independent) and shared across recipients.
/// `scratch_baseline_index` is a reusable `id → baseline index` table (length
/// covering the u16 id space, `-1` = absent); filled and reset within the call.
#[allow(clippy::too_many_arguments)]
pub fn select_for_delta(
    current: &Snapshot,
    baseline: &Snapshot,
    recipient: Vec2,
    confirmed_ids: &IdBitSet,
    current_ids: &IdBitSet,
    current_index_by_id: &[i32],
    ticks_since_last_sent: &HashMap<u16, u16>,
    enemy_byte_budget: usize,
    out_changed: &mut Vec<EnemyDeltaEntry>,
    out_removed: &mut Vec<u16>,
    out_added: &mut Vec<EnemySnap>,
    out_included_ids: &mut IdBitSet,
    scratch_scored: &mut Vec<ScoredEnemy>,
    scratch_baseline_index: &mut [i32],
) {
    crate::hot_span!("select_for_delta");
    out_changed.clear();
    out_removed.clear();
    out_added.clear();
    out_included_ids.clear();
    scratch_scored.clear();

    // Lane 1: removed = (confirmed | baseline) − current.
    if !confirmed_ids.is_empty() {
        for cid in confirmed_ids.iter() {
            if !current_ids.contains(cid) {
                out_removed.push(cid);
            }
        }
    } else {
        for e in baseline.enemies.iter() {
            if !current_ids.contains(e.id) {
                out_removed.push(e.id);
            }
        }
    }

    // id → baseline index table (reset at the end of the call).
    for (i, e) in baseline.enemies.iter().enumerate() {
        scratch_baseline_index[e.id as usize] = i as i32;
    }

    let mut removed_bytes = out_removed.len() * 2;
    if removed_bytes > enemy_byte_budget {
        let keepable = enemy_byte_budget / 2;
        if keepable < out_removed.len() {
            out_removed.truncate(keepable);
        }
        removed_bytes = out_removed.len() * 2;
    }
    let mut remaining = enemy_byte_budget.saturating_sub(removed_bytes);

    // Lane 2: spawns = current − confirmed. Score candidates, then take the
    // closest `min(MaxSpawns, fits)` via quickselect (uniform entry size). Once
    // the recipient has a confirmed set, pull the difference from the bitsets
    // (O(1024 words + spawns)) instead of scanning the whole enemy list.
    scratch_scored.clear();
    let have_confirmed = !confirmed_ids.is_empty();
    let mut push_spawn = |ci: usize| {
        let e = &current.enemies[ci];
        let dx = e.pos_x - recipient.x;
        let dy = e.pos_y - recipient.y;
        scratch_scored.push(ScoredEnemy {
            index: ci,
            score: dx * dx + dy * dy,
        });
    };
    if have_confirmed {
        for id in current_ids.iter_diff(confirmed_ids) {
            let ci = current_index_by_id[id as usize];
            if ci >= 0 {
                push_spawn(ci as usize);
            }
        }
    } else {
        // First delta after a full/reset — everything is a spawn.
        for i in 0..current.enemies.len() {
            push_spawn(i);
        }
    }
    let k_spawn = (remaining / ENEMY_SNAP_FULL_BYTES).min(MAX_SPAWNS_PER_SNAPSHOT);
    {
        let chosen = select_smallest(scratch_scored, k_spawn);
        for s in chosen.iter() {
            let e = current.enemies[s.index];
            out_added.push(e);
            out_included_ids.insert(e.id);
        }
        remaining -= chosen.len() * ENEMY_SNAP_FULL_BYTES;
    }

    // Lane 3: animation = confirmed ∩ baseline ∩ current, position changed.
    // Iterate the confirmed set directly (bounded by what the client has acked)
    // rather than scanning every current enemy; look up current position by id.
    scratch_scored.clear();
    if have_confirmed {
        for id in confirmed_ids.iter() {
            if scratch_baseline_index[id as usize] < 0 {
                continue;
            }
            let ci = current_index_by_id[id as usize];
            if ci < 0 {
                // Confirmed but gone from current — it's a removal (lane 1).
                continue;
            }
            let e = &current.enemies[ci as usize];
            let dx = e.pos_x - recipient.x;
            let dy = e.pos_y - recipient.y;
            let dist = (dx * dx + dy * dy).sqrt();
            let tsls = ticks_since_last_sent.get(&id).copied().unwrap_or(0);
            let score = dist - STALENESS_WEIGHT * (tsls as f32);
            scratch_scored.push(ScoredEnemy {
                index: ci as usize,
                score,
            });
        }
    }
    scratch_scored.sort_unstable_by(cmp_scored);

    let changed_entry_size = ENEMY_DELTA_ENTRY_BYTES;
    for s in scratch_scored.iter() {
        let e = current.enemies[s.index];
        let base_idx = scratch_baseline_index[e.id as usize] as usize;
        let b = &baseline.enemies[base_idx];
        if !enemy_position_changed(b, &e) {
            // Position unchanged after quantization — still counts as confirmed
            // for this recipient (the client already has the right value).
            out_included_ids.insert(e.id);
            continue;
        }
        if changed_entry_size > remaining {
            break;
        }
        remaining -= changed_entry_size;
        out_changed.push(EnemyDeltaEntry {
            id: e.id,
            pos_x: e.pos_x,
            pos_y: e.pos_y,
        });
        out_included_ids.insert(e.id);
    }

    // Reset the dirty entries of the shared index table for the next recipient.
    for e in baseline.enemies.iter() {
        scratch_baseline_index[e.id as usize] = -1;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::game::SnapshotKind;

    fn enemy(id: u16, x: f32, y: f32) -> EnemySnap {
        EnemySnap {
            id,
            pos_x: x,
            pos_y: y,
        }
    }

    fn snap_full(tick: u32, enemies: &[EnemySnap]) -> Snapshot {
        let mut s = Snapshot::with_capacity();
        s.kind = SnapshotKind::Full;
        s.tick = tick;
        s.enemies.extend_from_slice(enemies);
        s
    }

    #[test]
    fn full_selects_closest_first() {
        let cur = snap_full(
            1,
            &[
                enemy(1, 0.0, 0.0),
                enemy(2, 100.0, 0.0),
                enemy(3, 5.0, 0.0),
            ],
        );
        let mut out = Vec::new();
        let mut scratch = Vec::new();
        select_for_full(&cur, Vec2::ZERO, 12, &mut out, &mut scratch);
        let ids: Vec<u16> = out.iter().map(|e| e.id).collect();
        assert_eq!(ids, vec![1, 3]);
    }

    fn bits(xs: &[u16]) -> IdBitSet {
        let mut b = IdBitSet::new();
        for &x in xs {
            b.insert(x);
        }
        b
    }

    fn current_ids_of(s: &Snapshot) -> IdBitSet {
        let mut b = IdBitSet::new();
        for e in s.enemies.iter() {
            b.insert(e.id);
        }
        b
    }

    fn current_index_of(s: &Snapshot) -> Vec<i32> {
        let mut idx = vec![-1i32; 1 << 16];
        for (i, e) in s.enemies.iter().enumerate() {
            idx[e.id as usize] = i as i32;
        }
        idx
    }

    #[test]
    fn delta_classifies_removed_spawn_animation() {
        let baseline = snap_full(10, &[enemy(1, 0.0, 0.0), enemy(2, 5.0, 0.0)]);
        let current = snap_full(11, &[enemy(1, 0.5, 0.0), enemy(3, 10.0, 0.0)]);
        let confirmed = bits(&[1, 2]);
        let current_ids = current_ids_of(&current);
        let current_index = current_index_of(&current);
        let tsls: HashMap<u16, u16> = HashMap::new();
        let mut changed = Vec::new();
        let mut removed = Vec::new();
        let mut added = Vec::new();
        let mut included = IdBitSet::new();
        let mut scratch_scored = Vec::new();
        let mut scratch_baseline = vec![-1i32; 1 << 16];

        select_for_delta(
            &current,
            &baseline,
            Vec2::ZERO,
            &confirmed,
            &current_ids,
            &current_index,
            &tsls,
            10_000,
            &mut changed,
            &mut removed,
            &mut added,
            &mut included,
            &mut scratch_scored,
            &mut scratch_baseline,
        );

        assert_eq!(removed, vec![2u16]);
        assert_eq!(added.len(), 1);
        assert_eq!(added[0].id, 3);
        assert_eq!(changed.len(), 1);
        assert_eq!(changed[0].id, 1);
        assert!(included.contains(1));
        assert!(included.contains(3));
    }

    #[test]
    fn delta_skips_unchanged_position_but_marks_included() {
        let baseline = snap_full(10, &[enemy(1, 5.0, 0.0)]);
        let current = snap_full(11, &[enemy(1, 5.0, 0.0)]);
        let confirmed = bits(&[1]);
        let current_ids = current_ids_of(&current);
        let current_index = current_index_of(&current);
        let tsls = HashMap::new();
        let mut changed = Vec::new();
        let mut removed = Vec::new();
        let mut added = Vec::new();
        let mut included = IdBitSet::new();
        let mut scratch_scored = Vec::new();
        let mut scratch_baseline = vec![-1i32; 1 << 16];

        select_for_delta(
            &current,
            &baseline,
            Vec2::ZERO,
            &confirmed,
            &current_ids,
            &current_index,
            &tsls,
            10_000,
            &mut changed,
            &mut removed,
            &mut added,
            &mut included,
            &mut scratch_scored,
            &mut scratch_baseline,
        );

        assert!(changed.is_empty());
        assert!(removed.is_empty());
        assert!(added.is_empty());
        assert!(included.contains(1));
    }
}
