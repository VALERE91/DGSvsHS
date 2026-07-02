// Per-recipient snapshot broadcast. Port of
// csharp_arch_server/Net/QuicServer.cs::BroadcastSnapshot.
//
// For each connected slot:
//   1. Drain pending ack (set by the input dispatcher when the latest input
//      carried a higher `last_acked_server_tick`).
//   2. Decide Full vs Delta: Delta only if we have an ack > 0, the current
//      tick is at-or-after it, the gap is within MaxDeltaDepth, and the
//      baseline snapshot is still in the history ring.
//   3. Compute the per-recipient enemy byte budget given the fixed overhead.
//   4. Temporarily set Kind/BaselineTick/LastProcessedInputTick on the shared
//      scratch snapshot for encoding, then restore.
//   5. SelectForFull or SelectForDelta packs entities into the budget.
//   6. Encode header + body via the wire codec, emit as a NetMsgOut datagram.
//   7. Update RecipientSnapshotState with what was actually included.

use bevy::prelude::*;

use crate::game::constants::SNAPSHOT_BYTE_BUDGET;
use crate::game::constants::{MAX_DELTA_DEPTH, MAX_PLAYERS};
use crate::game::{EnemyDeltaEntry, EnemySnap, SnapshotKind};
use crate::network::codec::{
    write_delta_snapshot_body, write_full_snapshot_body, write_snapshot_header, FIRE_EVENT_BYTES,
    MSG_SNAPSHOT, PLAYER_SNAP_FULL_BYTES, SNAPSHOT_HEADER_BYTES,
};
use crate::network::{MsgKind, NetMsgOut, PlayerSlots};

use super::bitset::IdBitSet;
use super::capture::SnapshotScratch;
use super::history::WorldStateHistory;
use super::priority::{select_for_delta, select_for_full, ScoredEnemy};
use super::recipient::RecipientStates;

/// Reusable scratch buffers, owned by a Local<> so we don't allocate per tick.
pub struct BroadcastScratch {
    selected_enemies: Vec<EnemySnap>,
    scored: Vec<ScoredEnemy>,
    changed: Vec<EnemyDeltaEntry>,
    removed: Vec<u16>,
    added: Vec<EnemySnap>,
    included: IdBitSet,
    /// Current enemy ids, rebuilt once per tick (recipient-independent).
    current_ids: IdBitSet,
    /// id → current-enemy index, `-1` = absent. Built once per tick alongside
    /// `current_ids` and shared across recipients so the delta lanes can look up
    /// a current enemy by id in O(1) instead of scanning the whole enemy list.
    current_index: Vec<i32>,
    /// id → baseline index, `-1` = absent. Sized for the full u16 id space;
    /// `select_for_delta` fills and resets the entries it touches.
    baseline_index: Vec<i32>,
    encode_buf: Vec<u8>,
}

impl Default for BroadcastScratch {
    fn default() -> Self {
        Self {
            selected_enemies: Vec::new(),
            scored: Vec::new(),
            changed: Vec::new(),
            removed: Vec::new(),
            added: Vec::new(),
            included: IdBitSet::new(),
            current_ids: IdBitSet::new(),
            current_index: vec![-1; 1 << 16],
            baseline_index: vec![-1; 1 << 16],
            encode_buf: Vec::new(),
        }
    }
}

#[allow(clippy::too_many_arguments)]
pub fn broadcast_snapshot(
    mut scratch_res: ResMut<SnapshotScratch>,
    history: Res<WorldStateHistory>,
    mut recipients: ResMut<RecipientStates>,
    slots: Res<PlayerSlots>,
    mut out_msgs: MessageWriter<NetMsgOut>,
    mut work: Local<BroadcastScratch>,
) {
    let snap = &mut scratch_res.0;
    if snap.tick == 0 {
        // Nothing captured yet (server idle or pre-first-tick).
        return;
    }

    let players_bytes = 1 + snap.players.len() * PLAYER_SNAP_FULL_BYTES;
    let fires_count = snap.recent_fire_events.len().min(16);
    let fires_bytes = 1 + fires_count * FIRE_EVENT_BYTES;
    let fixed_overhead = 1 /* msg_type */ + SNAPSHOT_HEADER_BYTES + players_bytes + fires_bytes;

    // Recipient-independent: build the current-id set + id→index map once for
    // all recipients (reset touched entries after the loop below).
    work.current_ids.clear();
    for (i, e) in snap.enemies.iter().enumerate() {
        work.current_ids.insert(e.id);
        work.current_index[e.id as usize] = i as i32;
    }

    for pid in 0..MAX_PLAYERS as u8 {
        let Some(client_id) = slots.slot_client(pid) else {
            continue;
        };

        // Step 1: drain pending ack from the input dispatcher.
        let rstate = recipients.ensure(pid);
        if rstate.drain_pending_ack() {
            rstate.on_ack_advanced();
        }

        // Step 2: decide Full vs Delta.
        let acked_tick = rstate.last_acked_server_tick;
        let mut use_delta = false;
        let mut baseline_tick = 0u32;
        if acked_tick > 0
            && snap.tick >= acked_tick
            && snap.tick - acked_tick <= MAX_DELTA_DEPTH
            && history.try_get(acked_tick).is_some()
        {
            use_delta = true;
            baseline_tick = acked_tick;
        }

        // Recipient anchor — their player's position, or origin if not found.
        let mut anchor = Vec2::ZERO;
        for p in snap.players.iter() {
            if p.id == pid {
                anchor = Vec2::new(p.pos_x, p.pos_y);
                break;
            }
        }

        let enemy_section_header = if use_delta {
            2 /* changed */ + 2 /* removed */ + 2 /* added */ + 4 /* enemy_total */
        } else {
            2 /* enemy_count */ + 4 /* enemy_total */
        };
        let enemy_budget = SNAPSHOT_BYTE_BUDGET
            .saturating_sub(fixed_overhead)
            .saturating_sub(enemy_section_header);

        // Step 4: stamp recipient-specific header fields, encode, then restore.
        let saved_kind = snap.kind;
        let saved_baseline = snap.baseline_tick;
        let saved_lpi = snap.last_processed_input_tick;
        snap.kind = if use_delta {
            SnapshotKind::Delta
        } else {
            SnapshotKind::Full
        };
        snap.baseline_tick = if use_delta { baseline_tick } else { 0 };
        snap.last_processed_input_tick = recipients.highest_input_tick[pid as usize];

        let work: &mut BroadcastScratch = &mut work;
        work.encode_buf.clear();
        work.encode_buf.push(MSG_SNAPSHOT);
        write_snapshot_header(&mut work.encode_buf, snap);

        if use_delta {
            // try_get borrowed above is gone — re-borrow now that we hold the
            // budget split.
            let baseline = history.try_get(baseline_tick).unwrap();
            let rstate = recipients.ensure(pid);
            select_for_delta(
                snap,
                baseline,
                anchor,
                &rstate.confirmed_ids,
                &work.current_ids,
                &work.current_index,
                &rstate.ticks_since_last_sent,
                enemy_budget,
                &mut work.changed,
                &mut work.removed,
                &mut work.added,
                &mut work.included,
                &mut work.scored,
                &mut work.baseline_index,
            );
            write_delta_snapshot_body(
                &mut work.encode_buf,
                &snap.players,
                &work.changed,
                &work.removed,
                &work.added,
                snap.enemy_total_in_world,
                &snap.recent_fire_events,
            );
        } else {
            select_for_full(
                snap,
                anchor,
                enemy_budget,
                &mut work.selected_enemies,
                &mut work.scored,
            );
            write_full_snapshot_body(
                &mut work.encode_buf,
                &snap.players,
                &work.selected_enemies,
                snap.enemy_total_in_world,
                &snap.recent_fire_events,
            );
            work.included.clear();
            for e in work.selected_enemies.iter() {
                work.included.insert(e.id);
            }
            work.removed.clear();
        }

        // Restore the shared snapshot fields so the next recipient sees them
        // unmodified (matches Arch's BroadcastSnapshot pattern).
        snap.kind = saved_kind;
        snap.baseline_tick = saved_baseline;
        snap.last_processed_input_tick = saved_lpi;

        // Step 6: send.
        out_msgs.write(NetMsgOut {
            client: client_id,
            kind: MsgKind::Datagram,
            payload: work.encode_buf.clone(),
        });

        // Step 7: bookkeeping.
        let is_full = !use_delta;
        let snap_tick = snap.tick;
        let rstate = recipients.ensure(pid);
        rstate.on_snapshot_sent(snap_tick, is_full, &work.included, &work.removed);
    }

    // Reset only the id→index entries we touched this tick, so the shared map is
    // all-`-1` for the next tick without an O(id space) fill.
    for e in snap.enemies.iter() {
        work.current_index[e.id as usize] = -1;
    }
}
