# DGSvsHS Wire Format (v3)

Canonical engine-neutral specification of the DGSvsHS server↔client protocol. All three server implementations (Unity DOTS, Rust/Bevy/Avian, C#/Arch/Friflo/BepuPhysics) MUST produce bit-identical bytes for identical input. The shared Unity client decodes from this spec alone — no engine-specific extensions on the wire.

Byte ordering: **little-endian**. Float = IEEE 754 binary32. No padding inside structs.

---

## 1. Top-level framing

Each logical message is delivered as one transport packet/frame, prefixed with a single-byte type tag. Length is provided by the underlying transport (no inner length prefix):

```
struct Frame {
    u8   msg_type;
    u8[] payload;   // length-delimited by the transport
}
```

The transport choice (UDP, QUIC, etc.) is per-implementation; the bytes inside `payload` are identical.

### Message types

| Tag  | Direction | Name            | Reliability | Notes                                |
|------|-----------|-----------------|-------------|--------------------------------------|
| 0x01 | C → S     | `ClientHello`   | reliable    | First message after connect          |
| 0x02 | S → C     | `ServerWelcome` | reliable    | Assigns player id, echoes config     |
| 0x10 | C → S     | `Input`         | unreliable  | Sent each sim tick                   |
| 0x20 | S → C     | `Snapshot`      | unreliable  | Sent at snapshot rate (Full or Delta) |
| 0xF0 | S → C     | `Disconnect`    | reliable    | Reason byte                          |

---

## 2. Connection lifecycle

### `ClientHello` (0x01)
```
u32 protocol_version       // currently 3
u8  client_capabilities    // bitfield, reserved, send 0
```

### `ServerWelcome` (0x02)
```
u32 protocol_version
u8  assigned_player_id     // 0..MaxPlayers-1
u32 server_tick
u16 sim_tick_ms            // must equal Constants.SimTickMs
u16 snapshot_every_n_ticks // must equal Constants.SnapshotEveryNTicks
```

Mismatched `protocol_version`, `sim_tick_ms`, or `snapshot_every_n_ticks` → hard disconnect with `ProtocolError`. Runtime guard against silent drift between the three server implementations.

### `Disconnect` (0xF0)
```
u8 reason     // matches DisconnectReason enum values
```

---

## 3. `Input` (0x10) — client → server

Sent once per sim tick. Unreliable. The client redundantly includes the previous N inputs in each datagram so a single dropped packet doesn't lose state:

```
u8  redundancy_count        // 1..4
InputCmd cmds[redundancy_count]   // newest first
```

`InputCmd` (**25 bytes**):
```
u32 tick
u32 last_acked_server_tick   // highest server snapshot tick the client has fully reconstructed
f32 move_x                   // movement input, clamped server-side to magnitude ≤ 1
f32 move_y
f32 aim_x                    // unit-length world-space aim vector
f32 aim_y
u8  flags                    // bit 0 = Fire; other bits reserved, must be 0
```

Server dedup: inputs whose `tick` is ≤ the highest tick already processed for that player are discarded as processed-input commands. However, the **highest** `last_acked_server_tick` seen across the batch — including from dedup-dropped entries — is always promoted into the recipient state. The ack is monotonic and informative even from already-processed inputs.

---

## 4. `Snapshot` (0x20) — server → client

Sent every `Constants.SnapshotEveryNTicks` ticks. A snapshot is either a **Full** (self-contained) or a **Delta** (against an acked baseline).

### 4.1 Header (common, **24 bytes**)

```
u8   kind                       // 0 = Full, 1 = Delta
u32  current_tick
u32  baseline_tick              // valid only if kind == Delta; 0 if Full
u32  last_processed_input_tick  // per-recipient — highest input tick this client's inputs reached the sim
u16  round
f32  round_timer
f32  inter_round_timer
u8   phase                      // RoundPhase enum
```

### 4.2 Full body (kind == 0)

```
u8   player_count
PlayerSnapFull players[player_count]

u16  enemy_count                // count actually carried in this snapshot
u32  enemy_total_in_world       // telemetry — server's authoritative count (may exceed enemy_count when priority truncated)
EnemySnapFull enemies[enemy_count]

u8   fire_event_count           // 0..16
FireEvent fires[fire_event_count]
```

### 4.3 Delta body (kind == 1)

Players are always sent in full each delta tick (cheap — at most 40 bytes for 4 players — and simplifies join/leave handling on the client). Enemies are split into three lists; entities not appearing in any list retain their baseline values.

```
u8   player_count
PlayerSnapFull players[player_count]                   // current full set, replaces baseline players

u16  changed_enemy_count
EnemyDeltaEntry changed_enemies[changed_enemy_count]

u16  removed_enemy_count
u16  removed_enemy_ids[removed_enemy_count]

u16  new_enemy_count
EnemySnapFull new_enemies[new_enemy_count]

u32  enemy_total_in_world

u8   fire_event_count
FireEvent fires[fire_event_count]
```

### 4.4 Component layouts

`PlayerSnapFull` (**10 bytes**):
```
u8   id
i16  pos_x_mm                // pos meters × 1000, clamped to ±32.767 m
i16  pos_y_mm
i16  aim_angle               // atan2(aim_y, aim_x) × 10430 ≈ radians × 32768/π
u8   alive                   // 0 or 1
u16  disable_ticks           // round(seconds × TicksPerSecond), remaining disable+invulnerability
```

`EnemySnapFull` (**6 bytes**):
```
u16  id
i16  pos_x_mm
i16  pos_y_mm
```

`EnemyDeltaEntry` (**6 bytes**, fixed size):
```
u16  id
i16  pos_x_mm
i16  pos_y_mm
```

Entries are only emitted when the quantized position actually changed against the baseline. Position is the only mutable enemy field on the wire (v3 dropped velocity).

`FireEvent` (**14 bytes**):
```
u32  tick
u8   shooter_id
i16  origin_x_mm
i16  origin_y_mm
i16  dir_angle               // radians × 10430
i16  distance_mm             // typically BulletMaxRange (piercing beam)
u8   kill_count              // hint for client SFX/score; not authoritative
```

Piercing-beam model: no per-hit enemy ids on the wire. Kills surface via `EnemySnapFull` omission — the killed enemy disappears from the current set and shows up in the next delta's `removed_enemy_ids`. `kill_count` is a lightweight hint for client UX.

---

## 5. Per-recipient state and ack-driven confirmation

The server maintains per-recipient state to compose delta snapshots and to track which kill events the client has actually received. Every server implementation MUST mirror this state machine — otherwise mass-kill ticks will leave permanent ghost enemies on the client (server's view of "what the client has" drifts from reality).

### 5.1 State per recipient

```
last_acked_server_tick : u32                   // monotonic, pulled from incoming InputCmd
confirmed_ids          : Set<u16>              // enemy ids the client has acknowledged seeing
ticks_since_last_sent  : Map<u16, u16>         // staleness counter per confirmed id (saturates)
pending_sends          : Queue<PendingSend>    // unacked snapshots awaiting client confirmation
```

```
struct PendingSend {
    tick     : u32
    is_full  : bool
    included : Set<u16>    // enemy ids transmitted in that snapshot
    removed  : Set<u16>    // enemy ids removed in that snapshot
}
```

### 5.2 On snapshot send

Append a `PendingSend` entry with the ids actually placed on the wire this tick. Do **not** update `confirmed_ids` yet — confirmation requires an ack.

For each id in `confirmed_ids`: reset `ticks_since_last_sent[id] = 0` if included in this snapshot's wire set, else increment (saturating at u16 max). This is approximate ("ticks since we tried to send") and is good enough for priority scoring.

### 5.3 On ack advance

When an incoming `InputCmd.last_acked_server_tick` advances `last_acked_server_tick`, walk `pending_sends` and process every entry with `entry.tick ≤ last_acked`:

- **Full**: replace `confirmed_ids` with `entry.included` (a Full implicitly removes everything not in it). Reset all confirmed entries' staleness to 0.
- **Delta**: `confirmed_ids ∪= entry.included` and `confirmed_ids ∖= entry.removed`. Update staleness map symmetrically.

Drop processed entries from `pending_sends`.

### 5.4 Why this matters — the kill queue

Selecting the `removed_enemy_ids` for a Delta MUST iterate `confirmed_ids` (filtering out anything still in the current world), NOT the baseline snapshot's enemy set. Reason:

- `baseline = WorldStateHistory[last_acked_tick]` captures the world AT the ack tick.
- An enemy killed BEFORE `last_acked_tick` is already gone from `baseline.enemies`, so a baseline-iteration would never emit a remove for it.
- Meanwhile the client's actual cache, populated from snapshots before the kill, still has the enemy.
- Iterating `confirmed_ids` finds the discrepancy and emits the remove. If it doesn't fit in this tick's byte budget, it stays in `confirmed_ids` and retries next tick — guarantees death events reach the client within a bounded number of ticks regardless of burst size.

---

## 6. Priority + staleness selection (server)

For each recipient, on each snapshot tick:

1. **Baseline selection.** If `last_acked_server_tick == 0`, or `current_tick - last_acked > MaxDeltaDepth`, or the baseline snapshot is no longer in the server's history ring, send a Full. Otherwise look up the baseline snapshot at `last_acked_tick` and prepare a Delta.

2. **Byte budget.**
   ```
   enemyByteBudget = SnapshotByteBudget
                   - header_bytes
                   - player_block_bytes
                   - fire_block_bytes
                   - enemy_section_overhead
   ```
   `SnapshotByteBudget` must sit inside the transport's per-message limit. For the reference NGO/UDP transport in build 1 the cap is ~1280 bytes non-fragmented, so the budget is set to 1200 to leave headroom for transport framing.

3. **Removed lane (deterministic, ack-driven).** Iterate `confirmed_ids`; any id not in the current world is a removal candidate. Pack into `removed_enemy_ids` up to a per-tick share of `enemyByteBudget`. Truncated ids stay in `confirmed_ids` and reappear on the next tick.

4. **Spawn lane (new enemies).** Phase A. Build the set of current enemy ids NOT in `confirmed_ids`. Sort by distance² to recipient (closest first). Pack into `new_enemies`, capped at `MaxSpawnsPerSnapshot` per tick AND by remaining byte budget. Spawns truncated this tick remain absent from `confirmed_ids` and retry next tick.

5. **Animation lane (changed enemies).** Phase B. For each current enemy in `confirmed_ids` AND in `baseline.enemies`:
   ```
   score = distance(enemy.pos, recipient.player.pos)
         - StalenessWeight × ticks_since_last_sent[enemy.id]
   ```
   Lower score = higher priority. Sort ascending; pack into `changed_enemies` until budget is exhausted. Skip entries whose quantized position matches the baseline (no-op delta).

6. **Bookkeeping.** Record a `PendingSend` entry per §5.2.

**Why the staleness term.** A 25 m enemy reaches priority-equivalence with a 0 m enemy after `25 / StalenessWeight = 50` ticks (~0.8 s @ 16 ms). Without staleness, far enemies would never update; with it, every confirmed enemy gets refreshed within a bounded interval determined by the budget.

For a **Full** snapshot: rank by raw distance², pack closest enemies until budget exhausted, treat all transmitted enemies as "included" in the `PendingSend`. The Full's ack promotion implicitly removes anything not in `included`.

---

## 7. Rewind contract (server-side hit resolution)

All three server implementations MUST implement piercing-beam hit resolution with **bracketing-frame interpolation** against a recorded enemy-position history ring.

### 7.1 View-tick formula

```
fractional_view_tick = server_tick
                     - (one_way_latency_ms / 1000) × TicksPerSecond
                     - (InterpolationBufferMs / 1000) × TicksPerSecond
```

`one_way_latency_ms = transport_rtt_ms / 2`. Result is generally not an integer.

### 7.2 Bracketing-frame lookup

The history ring stores one frame per server tick for `SnapshotHistoryTicks` ticks. Each frame is `(tick, [(enemy_id, position)])` — positions only, captured at the END of that tick.

1. Find `floor_slot` = largest stored frame with `tick ≤ floor(fractional_view_tick)`.
2. Find `ceil_slot`  = smallest stored frame with `tick > floor(fractional_view_tick)`.
3. `alpha = (fractional_view_tick − floor.tick) / (ceil.tick − floor.tick)`, clamped to [0, 1].

**Fallbacks:**
- View-tick before oldest frame → clamp to oldest, alpha=0. Log the clamp event (high-latency tail telemetry).
- View-tick beyond newest frame → clamp to newest (defensive — should not happen, view-tick is always in the past).

### 7.3 Interpolated enemy set

Build a temporary list of `(id, position)` for hit resolution:
- Present in both `floor_slot` AND `ceil_slot`: `position = lerp(floor.pos, ceil.pos, alpha)`.
- `floor` only (died mid-bracket): include with `floor.pos`.
- `ceil` only (spawned mid-bracket): include only if `alpha ≥ 0.5`.

### 7.4 Beam test

- Beam segment: `origin → origin + dir × BulletMaxRange`.
- Per enemy in interpolated set, test segment-vs-circle with hit radius = `EnemyRadius + BeamRadius`.
- **Piercing**: do NOT stop on first hit. Collect ALL enemies within hit radius along the segment.
- Apply kills to the CURRENT authoritative world by enemy id (not to the interpolated set).
- Emit one `FireEvent` per beam with `kill_count` = number of enemies killed.

**No friendly fire** — other players are never candidates.

**Why bracketing.** Nearest-frame snapping introduces up to half a tick (~8 ms @ 16 ms tick) of position error. At edge-of-hit distances this flips hit/miss inconsistently. Bracketing eliminates the rounding error entirely. All three servers MUST use bracketing — mixed approaches contaminate the comparison.

---

## 8. Determinism contract

### 8.1 RNG

- Algorithm: **xoroshiro128+**.
- Seed: SplitMix64 expansion of a single `u64` trial seed.
- All three implementations MUST produce identical 64-bit outputs for identical seeds.

### 8.2 Sim sub-step order per tick

Engine-agnostic. Each tick, in this exact order:

1. Advance world tick (`Tick++`) and clear the per-tick fire-event accumulator.
2. Round director: phase transitions (`InterRound ↔ InRound`), staggered enemy spawns. Spawn placement consumes one `NextRange(0, 2π)` RNG draw per enemy — order must match across implementations.
3. Drain inputs for this tick, applying latest per-player move/aim/cooldown, queueing Fire commands for rewind.
4. Rewind hit resolution for queued Fire commands (per §7); destroy killed enemies.
5. Enemy AI: per enemy, set velocity to unit-vector-to-nearest-alive-non-disabled-player × `EnemySpeed`. If no valid target, velocity = 0.
6. Enemy integrate: `position += velocity × SimDt`.
7. Player-enemy contact: per alive non-disabled player, if any enemy within `(PlayerKillRadius + EnemyRadius)`, set `DisableTimer = DisableDurationSec`. (Skip the whole step in God Mode.)
8. Record post-step world into the rewind ring.
9. Compose + broadcast per-recipient snapshot.

Any deviation from this order between the three servers — including reordering RNG-consuming substeps — contaminates the comparison.

### 8.3 Server lifecycle

The server is a state machine: `Booting → Idle → Running → Resetting → Idle → … → ShuttingDown`. Sim sub-steps above run only in `Running`. The transport accepts client connections in every state except `ShuttingDown`. `Resetting` is a single-tick transient that destroys all enemies, reseeds the RNG to the trial seed, and clears the rewind ring — used when the last client disconnects. All three implementations MUST emit a transition log line `state: X → Y tick=N` so trial timelines align across builds.

---

## 9. Quantization invariants

- **Position**: `i16 = round(meters × 1000)`. Range ±32.767 m. Arena radius is 25 m; full arena fits with margin.
- **Aim angle**: `i16 = round(radians × 10430)`. Full circle (`2π`) maps to ≈ 65360 units → 1 unit ≈ 0.0055°.
- **Disable timer**: `u16 = round(seconds × TicksPerSecond)`. `DisableDurationSec = 10` → max 625 ticks; fits easily.
- **Quantization equality** drives delta change-detection: enemies whose quantized position rounds to the same i16 as in baseline are treated as unchanged and do not contribute a wire entry. Avoids spamming the wire with floating-point noise.
