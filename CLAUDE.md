# CLAUDE.md — DGSvsHS Project Context

This document is the handoff context for any AI assistant working on this project. Read it fully before making changes. It captures the experimental design, the architectural decisions, the current state of the codebase, the in-flight refactor, and the bugs/limitations to watch out for.

---

## 1. Project purpose

**DGSvsHS** is the Unity-side codebase for Samuel's master's research at UQAC (Département DIM) on **heterogeneous game-server architecture vs. monolithic dedicated game servers**. The deliverable is a paper measuring **server-side power consumption** ("green computing") for a fixed gameplay workload, comparing **three server architectures**, all ECS-based so the comparison isolates language/runtime/physics-engine variables rather than paradigm differences:

- **Build 1 (Unity DOTS baseline):** Unity Dedicated Server using **Unity DOTS / Entities** for the sim (NOT MonoBehaviour-driven `List<EnemyState>` — that would be an unfair paradigm mismatch against the other two pillars). Networking: NGO + UnityTransport over UDP. Physics: hand-rolled (Burst-compiled IJobEntity for seek/integrate + spatial grid). The "what game studios do today using ECS" reference.
- **Build 2 (research contribution A — Rust):** **Bevy ECS** server in Rust over QUIC. Physics: **Avian** (the Bevy-native Rapier successor). Headless. The orchestrator + workers heterogeneous design from Samuel's PSDRC 2026-2027 proposal.
- **Build 3 (research contribution B — C# ECS):** **Arch ECS** + **Friflo** (engine/persistence) + **BepuPhysics v2** in plain .NET (no Unity runtime). Headless. The "what if we kept the C# language but escaped Unity" data point.

The **client is the same Unity binary** for all three server builds. Only the transport endpoint differs.

**The wattmeter measures the server machine.** Clients run on a separate host (Hetzner box, or LXCs on Samuel's `pve01` Proxmox homelab) so client load doesn't contaminate the server measurement.

**ECS-parity rule:** every gameplay decision that affects server CPU/memory cost must land in all three implementations with equivalent semantics. The wire format (`WireFormat.md`) is the explicit contract; the gameplay model (§2 below) is the implicit one. If you change one and forget to mirror, the comparison is invalid.

---

## 2. Gameplay model (locked, do not change without explicit instruction)

### High-level

- **2D top-down survivor-shooter.** Up to 4 players cooperate (no PvP, no friendly fire).
- **Server-authoritative** for everything. Client predicts only its own player's movement + fire.
- **10 rounds** per match, escalating enemy counts. AI scaling `BaseEnemiesPerRound * EnemyScalingPerRound^(round-1)`.
- **Server is idle when no clients connected** (no enemies, round 0). First client connection triggers round 1 via a 3s `InterRound` countdown.
- **Last client disconnects** → server resets RNG to original seed, wipes state, returns to idle. Reproducible trials.

### Controls

- WASD → world-space 8-directional movement
- Mouse → twin-stick aim (aim vector = `normalize(mouseWorld - playerWorld)`)
- LMB held → fire

### Combat

- **Hitscan piercing laser**, no projectile entities. Per-fire cooldown (`PlayerFireCooldownSec = 0.12s`).
- Beam goes from player to `BulletMaxRange` (50m) and **kills every enemy in its path** (segment-vs-circle test with `EnemyRadius + BeamRadius` clearance).
- **Fire prediction** is client-side, zero-delay: when local input has Fire and predicted cooldown is 0, the beam is rendered immediately at the predicted position. Server's confirmation event is *filtered out for the local player* in the snapshot to avoid double-rendering.
- Remote players' beams come from `FireEvent`s in server snapshots.

### Disable mechanic (not "death")

- Player touches enemy → `DisableTimer = 10s`. Player stays alive, can move, can aim. **Fire is blocked.** Player is **invulnerable** while disabled (additional enemy touches don't refresh the timer; enemy AI ignores disabled players as targets).
- Timer decays at `SimDt` per tick. Hits 0 → fire returns automatically.
- **All connected players simultaneously disabled** → team wipe: reset to round 1, clear all enemies and all DisableTimers, restart cleanly. (Not "Defeat" — there is no Defeat phase anymore in PvE play.)

### Lag compensation: bracketing-frame rewind

- Server records full enemy state per tick into a ring buffer (`LagCompBuffer`).
- On Fire input, computes **fractional view-time** = `serverTick - (oneWayLatencyMs + InterpolationBufferMs) × TicksPerSecond / 1000`.
- Finds the two stored frames bracketing that fractional tick (`floor` and `ceil`).
- Builds an interpolated enemy list (lerps position+velocity at fractional alpha). Enemies in only-floor (died mid-bracket) are included with floor position; enemies in only-ceil (spawned mid-bracket) included only if alpha ≥ 0.5.
- Resolves the piercing beam against the interpolated set. Kills are applied to the **current** world by enemy id ("shot around the corner" tradeoff).
- View-time older than the buffer window → clamp to oldest frame (NOT fall back to current-world; that would silently advantage high-latency shooters).

### Determinism

- All RNG goes through `DeterministicRng` (xoroshiro128+, SplitMix64-seeded). Same seed produces identical enemy spawns on both servers.
- Both servers must run **identical sub-step order** per tick (documented in `WireFormat.md §7`).

---

## 3. Architecture summary

**Two orthogonal state machines:**
- **Server lifecycle (in `DedicatedServerMain`)** — `Booting → Idle → Running → Resetting → Idle → … → ShuttingDown`. Drives whether the DOTS World ticks. Idle polls NGO for incoming connections but doesn't run the sim. Running ticks + broadcasts. Resetting is a single-frame transient that wipes enemies + reseeds RNG. Every transition logged as `[Server] state: X → Y tick=N` for trial timeline correlation. Mirror with the same state names in Bevy and Arch builds.
- **Gameplay round phase (in `RoundState.Phase`)** — `PreGame → InterRound → InRound → InterRound → … → Victory`. Only advances inside server-state = Running. CoD-Zombies-style: a round ends when (`SpawnsRemaining == 0` AND no alive enemies). All-players-disabled triggers `ResetToRoundOne`.

**File layout note.** This `CLAUDE.md` lives at `B:/DGSvsHS/CLAUDE.md` (parent of the Unity project folder). The Unity project itself is at `B:/DGSvsHS/DGSvsHS/`. Paths in this section are relative to **the Unity project root** (`B:/DGSvsHS/DGSvsHS/`).

```
Assets/_Game/
├── Gameplay/                  PURE — no Unity engine state, no MonoBehaviours, no networking
│   ├── Constants.cs           Single source of truth; Bevy mirrors values
│   ├── DeterministicRng.cs    xoroshiro128+ (matches Rust's rand_xoshiro::Xoroshiro128Plus)
│   ├── InputCmd.cs            Client→server input wire type
│   ├── Snapshot.cs            Server→client world-state wire type
│   ├── SimState.cs            POCOs: PlayerState, EnemyState, FireEvent, WorldState
│   ├── Sim.cs                 Pure step function, decomposed into sub-steps
│   ├── RoundDirector.cs       Wave spawning, scaling
│   ├── LagComp.cs             Bracketing-frame rewind for hitscan
│   ├── SpatialGrid.cs         Uniform grid; Rapier-equivalent query shapes
│   └── DGSvsHS.Gameplay.asmdef
│
├── Net/                       Transport-agnostic interfaces + wire codec
│   ├── INetworkClient.cs      Client seam
│   ├── INetworkServer.cs      Server seam (Build 1 only; Build 2 server is Bevy)
│   ├── WireCodec.cs           Byte-level encode/decode (same bytes for NGO and QUIC paths)
│   ├── WireFormat.md          Canonical protocol spec — Rust side implements from this
│   ├── DGSvsHS.Net.asmdef
│   ├── Ngo/                   Build 1 transport (#if WITH_DGS)
│   │   ├── NgoNetworkClient.cs
│   │   ├── NgoNetworkServer.cs
│   │   └── DGSvsHS.Net.Ngo.asmdef    references Unity.Netcode.Runtime + Unity.Collections
│   └── Quic/                  Build 2 transport (#if !WITH_DGS)
│       ├── QuicNetworkClient.cs      P/Invoke shim onto Rust cdylib "dgsvshs_socket"
│       └── DGSvsHS.Net.Quic.asmdef
│
├── Client/                    Shared by both builds
│   ├── ClientMain.cs          MonoBehaviour orchestrator
│   ├── ClientSimulation.cs    Pure prediction/reconciliation/interpolation state machine
│   ├── PlayerInputReader.cs   Input System (WASD + mouse + LMB)
│   ├── Views/
│   │   ├── SpriteViewPool.cs  Pooled circle sprites for players + enemies
│   │   └── BeamViewPool.cs    LineRenderers for laser visuals
│   └── DGSvsHS.Client.asmdef
│
├── Server/                    Build 1 DGS code (#if WITH_DGS)
│   ├── DedicatedServerMain.cs MonoBehaviour: owns WorldState, runs sim loop, broadcasts snapshots
│   └── DGSvsHS.Server.asmdef
│
└── Harness/                   Benchmark trial coordination
    ├── AutoPilot.cs           Deterministic bot (orbit + fire-at-nearest)
    ├── TrialRunner.cs         CLI args, NDJSON logging, trial timing
    └── DGSvsHS.Harness.asmdef
```

### Scripting defines

- **`WITH_DGS`** — must be set on:
  - **Windows** platform (Project Settings → Player → Other Settings → Scripting Define Symbols)
  - **Windows Server** platform (same place but with the Windows Server platform tab selected)
  - Any other platform you build for Build 1

  When set, NGO transport compiles, QUIC transport is excluded. Remove this define for Build 2 client builds.

- **`UNITY_SERVER`** — auto-set by Unity when building for the Dedicated Server platform target.

### Two-scene model

- `Assets/Scenes/Server.unity` — server-only. Contains: `NetworkManager` (with `UnityTransport`), `Server` GameObject (with `DedicatedServerMain` component).
- `Assets/Scenes/Client.unity` — client-only. Contains: `NetworkManager` (with `UnityTransport`), `Client` GameObject (with `ClientMain`), `Main Camera`, optional `TrialRunner` for benchmark.

Build profiles list only their own scene. Both profiles share the `WITH_DGS` define.

### Critical inspector settings (NetworkManager component, both scenes)

- **Connection Approval**: UNCHECKED (we don't use NGO approval; slot assignment is in `OnClientConnectedNgo`)
- **Force Same Prefabs**: UNCHECKED (we don't use NGO prefab replication)
- **Enable Scene Management**: UNCHECKED (CRITICAL — leaving this on causes NGO to try to load the server's scene on the client, throwing `Scene Hash X not in HashToBuildIndex table`)
- **UnityTransport → Max Payload Size**: **65536** (default 6144 is too small for our snapshots)
- **UnityTransport → Address**: `0.0.0.0`, **Port**: `7777`

Code-level redundancy: `NgoNetworkServer.Start` and `NgoNetworkClient.Connect` both force `NetworkConfig.ConnectionApproval = false` and `NetworkConfig.EnableSceneManagement = false` before `StartServer/Client`. The inspector settings are belt-and-suspenders; the code is the source of truth.

---

## 4. Where we are right now (state at handoff)

### What works end-to-end

- Server starts, listens on UDP 7777, logs `[DedicatedServerMain] Listening on port 7777, seed C0FFEEF00D`.
- Client connects from another process (separate `.exe` or Multiplayer Play Mode), gets player slot, receives `ServerWelcome`.
- First client triggers round 1; enemies spawn at arena rim and chase players.
- Hitscan piercing beam kills enemies; lag-comp rewind active.
- Disable mechanic: walk into an enemy → 40% alpha fade, can move but not fire, 10s timer, then back.
- Last client disconnects → server resets RNG, clears state, returns to idle.
- Local player fire prediction is zero-delay (predicted beam spawns immediately client-side).
- P2's beams ARE now visible to P1 (fixed by moving `FireEvents.Clear()` out of `Sim.Step` into `Sim.ClearTransients()` called at top of server tick).

### Constants for the current state

- `SimTickMs = 16` (integer ms, exactly), `TicksPerSecond = 62.5`
- `SnapshotEveryNTicks = 1` (snapshot every tick)
- `InterpolationBufferMs = 100f`
- `MaxEnemies = 131072` (simulation ceiling)
- `MaxPlayers = 4`
- Wire format is **still v1**: f32 positions, f32 velocity on enemies, full-state snapshots, no delta encoding, no priority truncation.

### Known limitations / things that work but aren't ideal

- **Snapshots are full-state (no delta encoding).** At MaxEnemies=131072 the wire bytes explode. Practical ceiling at current `MaxPayloadSize=65536` and v1 19-byte-per-enemy encoding is **~3000 enemies visible per client** before snapshots are dropped (with a logged warning, not a crash).
- **No priority/relevance scoring** — all enemies go into snapshots until budget is hit, then drops the tail.
- **Inputs don't carry server ack** (no `last_acked_server_tick` field). Server doesn't know what client has received.
- **Snapshot rate = sim rate.** ~62 snapshots/sec per client × ~19 bytes × N enemies. Bandwidth-heavy.
- **The codebase already has the constants for v2 (PositionScale, VelocityScale, AngleScale, StalenessWeight, MaxDeltaDepth, SnapshotByteBudget) but the *protocol* still uses v1 layout.** The Constants are declared but unused. This is intentional — v2 work is staged for the next session.

---

## 5. The pending v2 wire format refactor — what's planned

This is the **next major work item.** It was scoped, agreed in detail, and deferred mid-session because of conversation length. Specifics are committed:

### Goals

1. **Compact wire encoding** — quantize positions to i16 (mm precision, ±32m range), velocity to i8 (×50 scale, ±2.54 m/s), aim to i16 (radian × `AngleScale ≈ 10430`), DisableTimer to u16 ticks. Drop `hp` from `EnemySnap` entirely (always 1 in PvE).
2. **Delta encoding (option (c) — sequence-based with full-snapshot fallback).** Each snapshot is either a "full" snapshot or a "delta" against a previous acked snapshot. If gap exceeds `MaxDeltaDepth = 32` ticks, server falls back to full.
3. **Input-piggybacked acks.** Client adds `LastAckedServerTick: u32` field to `InputCmd`. Server tracks `lastAckedTick[playerId]`, uses it as the delta baseline.
4. **Priority/staleness scoring** for entities. Score = `distance_to_recipient - StalenessWeight × ticks_since_last_sent_to_this_recipient`. Lower score = higher priority. Pack the byte budget by ascending score. Far-away entities still get refreshed eventually (staleness boost overcomes distance penalty).
5. **Shared world-state history ring** across recipients (25 MB total at full MaxEnemies, not per-recipient).
6. **Players also delta-encoded** (only changed fields shipped, with a change_mask byte per included entity).

### Wire spec for v2 snapshot (already designed, not yet implemented in code)

```
Snapshot header:
  u8   kind                    // 0 = full, 1 = delta
  u32  current_tick
  u32  baseline_tick           // valid only if kind == 1; 0 if full
  u32  last_processed_input_tick
  u16  round
  f32  round_timer
  f32  inter_round_timer
  u8   phase

If kind == 0 (full):
  u8   player_count
  PlayerSnapFull players[player_count]
  u16  enemy_count                  // included in this snapshot
  u32  enemy_total_in_world         // telemetry — how many the server has authoritative state for
  EnemySnapFull enemies[enemy_count]
  u8   fire_event_count
  FireEvent fires[fire_event_count]

If kind == 1 (delta):
  u8   changed_player_count
  {u8 id, u8 mask, ...changed fields...} changed_players[]
  u16  changed_enemy_count
  {u16 id, u8 mask, ...changed fields...} changed_enemies[]
  u16  removed_enemy_count
  u16  removed_enemy_ids[removed_enemy_count]
  u16  new_enemy_count
  EnemySnapFull new_enemies[new_enemy_count]
  u8   fire_event_count
  FireEvent fires[fire_event_count]
```

`EnemySnapFull` (v2, 8 bytes):
```
u16  id              2
i16  pos_x_mm        2
i16  pos_y_mm        2
i8   vel_x_q50       1
i8   vel_y_q50       1
```

`PlayerSnapFull` (v2, 10 bytes):
```
u8   id              1
i16  pos_x_mm        2
i16  pos_y_mm        2
i16  aim_angle       2     // radians × AngleScale (i16)
u8   alive           1
u16  disable_ticks   2     // ticks remaining of disable (0..600 for 10s @ 16ms)
```

`FireEvent` (v2, 14 bytes):
```
u32  tick            4
u8   shooter_id      1
i16  origin_x_mm     2
i16  origin_y_mm     2
i16  dir_angle       2
i16  distance_mm     2
u8   kill_count      1
```

Change masks for delta entities use bit-per-field encoding:
- Enemy mask: bit 0 = pos changed, bit 1 = vel changed
- Player mask: bit 0 = pos, bit 1 = aim, bit 2 = disable_timer, bit 3 = alive

### Files that need to be created or modified for v2

| File | Action |
|---|---|
| `Constants.cs` | Already updated (constants declared) ✓ |
| `InputCmd.cs` | Add `LastAckedServerTick: uint` field |
| `Snapshot.cs` | Add `Kind` (enum: Full/Delta), `BaselineTick`, distinguish full vs. delta body |
| `WireCodec.cs` | Quantize encode/decode; full + delta snapshot write/read paths |
| `WireFormat.md` | Bump to v2; document the delta protocol, quantization, ack semantics |
| **NEW** `WorldStateHistory.cs` | Ring of past `WorldState` snapshots (or just enemy + player state), shared across recipients |
| **NEW** `SnapshotPriority.cs` | Pure scoring + selection algorithm; for Bevy parity |
| **NEW** `RecipientSnapshotState.cs` | Per-recipient: `lastAckedTick`, `ticksSinceLastSent[entityId]` for staleness |
| `NgoNetworkServer.cs` | Reads ack from incoming Input messages; composes per-recipient snapshot using priority + delta; passes byte budget |
| `DedicatedServerMain.cs` | Owns the history ring; calls per-recipient capture instead of single shared `_snapshotScratch` |
| `ClientSimulation.cs` | Decodes both full + delta; maintains its own baseline state; provides `LastAckedServerTick` to the next outgoing input |
| `PlayerInputReader.cs` / `AutoPilot.cs` | Threads the ack value into produced InputCmd |
| `ClientMain.cs` | Pulls `LastAckedServerTick` from `ClientSimulation` when sampling input |

**Estimated complexity:** ~10 files touched, 3 new files, ~3-4 hours of focused generation work in a fresh conversation. Each file is well-scoped individually; the coupling between them is what makes it best done in one focused pass.

---

## 6. How to continue the v2 work in a fresh Claude Code session

1. Confirm the project compiles and runs as documented in §4 (server starts, client connects, gameplay works, two clients can see each other's beams).
2. Tell Claude: *"Implement v2 wire format per `CLAUDE.md` §5 — quantization, delta encoding with input-piggybacked acks, priority+staleness selection. The plan is fully specified; produce all 10-13 files."*
3. The agent should:
   - Read `Constants.cs` first to confirm the constants are present.
   - Read `WireFormat.md` to see the v1 baseline.
   - Implement in dependency order: `InputCmd` → `Snapshot` → `WireCodec` → history ring → priority → server → client.
   - **NOT** change the gameplay semantics (`Sim.cs`, `LagComp.cs`, `RoundDirector.cs`, `SpatialGrid.cs` should remain untouched).
   - Update `WireFormat.md` last, reflecting the actually-implemented protocol.
4. After generation, verify:
   - 2 clients running against built server, both can see each other.
   - Latency on disconnect → next reconnection still works (delta baseline reset path).
   - Snapshot sizes drop substantially in steady-state (delta carrying only changed entities).
   - Server NDJSON log shows `clamp_to_oldest` events near zero under normal latency.

---

## 7. Notable bugs we've already fixed (so they don't get re-introduced)

These are the lessons learned in this project; please don't undo them:

1. **`MemoryStream(buffer)` is non-expandable.** Our scratch buffers must be sized for worst-case payloads. NGO server scratch = 64KB, client input scratch = 256 bytes. If MaxEnemies grows, revisit `_scratch` size in `NgoNetworkServer.cs`.

2. **NGO's `CustomMessagingManager` is null until after `StartServer`/`StartClient`.** Register named-message handlers AFTER, not before.

3. **NGO's `NetworkConfig` is locked at `StartServer`/`StartClient`.** Configure it BEFORE. Currently both sides force `ConnectionApproval = false` and `EnableSceneManagement = false` programmatically.

4. **NGO connection approval performs a strict `NetworkConfig` hash check** that silently drops clients with no useful log. We disable approval entirely; player slots are assigned in `OnClientConnectedNgo`.

5. **NGO `EnableSceneManagement = true`** + server scene not in client's build → "Scene Hash X not in HashToBuildIndex table" exception → connection torn down. We force-off in code.

6. **`Sim.Step` previously cleared `world.FireEvents` as its first action.** Lag-comp resolves fires BEFORE `Sim.Step`, so they were wiped before being snapshotted → remote players never saw each other's beams. We now call `Sim.ClearTransients(world)` at the **start** of the server tick, and `Sim.Step` no longer clears.

7. **`Force Same Prefabs` in NetworkManager** rejects clients with any prefab-list difference. We disable.

8. **UnityTransport's default `Max Payload Size = 6144`** is too small. We need 65536.

9. **`FindObjectOfType` is deprecated in Unity 6.** Use `FindFirstObjectByType<T>()`.

10. **Reconciliation replay must NOT re-emit predicted fires.** `ApplyInputToPredictedLocalPlayer` takes an `emitPredictedFire` flag. Live-input path = true; replay path = false. Otherwise the local player sees a flickery double-beam every snapshot.

11. **Server-emitted `FireEvent`s for the local player must be filtered out** in `OnSnapshotReceived` to avoid double-beam on top of the predicted one.

---

## 8. Conventions and style

- **No NGO replication features.** No `NetworkBehaviour`, no `NetworkVariable`, no `NetworkObject`. NGO is reduced to a transport. Same goes for the QUIC side — our wire format is the source of truth.
- **Pure layer / impure layer separation.** Anything in `Gameplay/` must compile without referencing `UnityEngine.*` types beyond `Vector2`, `Mathf`. No MonoBehaviours, no `Transform`, no `GameObject`. This keeps the layer reimplementable in Rust on the Bevy side.
- **Constants in one place.** `Gameplay/Constants.cs`. If you need a new tunable, add it there.
- **Bevy parity is the rule, not the exception.** Any change to wire format, sim order, RNG usage, or determinism-critical math MUST be reflected in `WireFormat.md` so the Rust side can match.
- **The user (Samuel) writes terse, technical, prefers minimal targeted code changes over architectural rewrites.** Push back when over-engineering creeps in.
- **The user codes/debugs in English but writes academic and user-facing content in French.** Code and comments stay English.

---

## 9. Out-of-scope (do not implement unless explicitly asked)

- **Visual polish**: particle effects, screen shake, hit confirmation UI. Game is a benchmark first.
- **Audio**: not necessary for power-comparison trials.
- **Networked physics**: we use AI seeking + circle collisions, not Rapier/PhysX. Bevy side may use Rapier as a query backend (for `intersections_with_shape` / `cast_shape`) but the *physics simulation* is hand-rolled to keep behavior identical across builds.
- **Cheat protection**: irrelevant; no players outside Samuel + bots.
- **Matchmaking, lobbies**: skipped per design. First-connect-starts-the-match.
- **Reconnection-with-state-recovery**: not needed for trials. Lost connection → trial restart.

---

## 10. Quick reference: how to run the project today

All paths are relative to the **Unity project root** (`B:/DGSvsHS/DGSvsHS/`).

### Server (built)
1. Build profile `Server-Windows` (or Linux/macOS Server), scene list = `Assets/Scenes/Server.unity` only.
2. `WITH_DGS` define set for Windows Server platform.
3. Run the produced `.exe` from a terminal (Samuel's convention so far: `B:/DGSvsHS/Build/<date>/Server/DGSvsHS.exe`).
4. Watch for `[DedicatedServerMain] Listening on port 7777, seed C0FFEEF00D`.

### Client (editor or built)
1. Either: open `Assets/Scenes/Client.unity` in editor and press Play, or build profile `Client-Windows` and run the `.exe`.
2. `Host = 127.0.0.1`, `Port = 7777` on the `ClientMain` component.
3. WASD to move, mouse to aim, LMB to fire.

### Benchmark trial (autopilot)
1. Build `Client-Windows`.
2. Run with CLI: `DGSvsHS.exe --server 127.0.0.1 --port 7777 --bot-id 0 --seed 1 --duration 300 --output trial.ndjson`.
3. Repeat with `--bot-id 1, 2, 3` for 4-bot trials. Each bot has a deterministic orbit derived from `seed ^ botId`.

### §10.1 MicroVM build + Proxmox deployment

The three `build_microvm_aarch64.sh` scripts (Bevy, Arch, Unity DGS) each output two files into a leg-local `.microvm_aarch64/` working dir:

- `vmlinuz-virt` — Alpine `linux-virt` aarch64 kernel (~10 MB)
- `initramfs.cpio.gz` — rootfs + `/init` (Rust = ~5 MB; Arch = ~80 MB; Unity = ~200 MB+ depending on Data/)

These are NOT a turnkey Proxmox VM template (no qcow2, no GRUB, no `/sbin/init`). The kernel boots the initramfs directly; there is no disk image.

**Standalone QEMU (what the script does by default).** The script ends by booting via `qemu-system-aarch64 -nographic -append "console=ttyAMA0 …"`. All `printf`/log output from the server binary lands in the terminal where the script was launched (`logFile /dev/stdout` for Unity; default stdout for the others). Ctrl-A then X to kill QEMU.

**Architecture caveat.** Scripts produce aarch64 microvms. Proxmox VE is x86_64-only in practice. Running an aarch64 microvm on pve01 falls back to QEMU TCG (software emulation, 10–100× slowdown) — invalidates power trials. Two options:
- Run on an aarch64 host (Hetzner Ampere box, Apple Silicon Mac with `accel=hvf`)
- Port the scripts to x86_64 (see §11.3 #3 for the swap list)

**Proxmox VE direct-kernel-boot deployment (works for aarch64 only if the Proxmox host is aarch64).** Proxmox doesn't expose direct-kernel-boot in the web UI but its underlying QEMU does. Procedure on the Proxmox host:

1. `scp .microvm_aarch64/vmlinuz-virt .microvm_aarch64/initramfs.cpio.gz root@pve01:/var/lib/vz/template/microvm/`
2. Create a placeholder VM in the UI (any guest OS; no disk needed — but the UI requires one to save, so attach a 1 GB scratch disk you'll never use).
3. Edit `/etc/pve/qemu-server/<VMID>.conf` and append:
   ```
   args: -kernel /var/lib/vz/template/microvm/vmlinuz-virt -initrd /var/lib/vz/template/microvm/initramfs.cpio.gz -append "console=ttyS0 panic=1 cgroup_disable=memory,pids"
   serial0: socket
   ```
   `console=ttyS0` (not `ttyAMA0`) because Proxmox's q35 machine uses an ISA serial port.
4. In the UI, set **Hardware → Display → Serial terminal (serial0)**. This makes the web UI "Console" tab attach to the serial socket.
5. Forward UDP 7777 (DGS) / 7778 (Arch) / 4433 (Bevy) on the Proxmox firewall to the VM's NIC.
6. Start the VM.

**Viewing logs in Proxmox:**
- Web UI: **VM → Console** tab (with `serial0` set as display). Shows the boot sequence + every server log line.
- CLI: `ssh root@pve01` then `qm terminal <VMID>` — same serial console in your SSH session. Ctrl-O to exit.
- Persistent: `qm config <VMID>` to check serial is wired; logs are not retained anywhere on the host by default (initramfs `/tmp` is tmpfs). To persist, redirect stdout inside `/init` to a virtio-9p shared host folder or add a virtio-blk disk and mount it.

**Bypass-Proxmox path (simplest for debugging).** SSH into the Proxmox host directly and run the same `qemu-system-aarch64 …` line the script uses. Logs come back through the SSH terminal. No VM gets created in the Proxmox UI. Useful when you just want to verify the microvm boots cleanly before automating the deployment.

---

## 11. Session log — 2026-05 to 2026-06 (v4 wire format + Arch full parity)

This section supersedes §4–§6 for "where we are right now." The earlier §5 v2 wire-format plan is **done and extended to v4** (smaller InputCmd than v2 proposed). All three legs are wire-compatible.

### 11.1 Completed in this session

- **Arch server (`csharp_arch_server/`) brought to behavioral parity with DGS.** Full sim port (lifecycle SM + round director + lag-comp rewind + 9 systems), then a QUIC server on **StirlingLabs.MsQuic 23.7.1**, then per-recipient snapshot machinery matching `NgoNetworkServer`.
- **QUIC stack choice settled.** Bevy uses `quiche` (BoringSSL). Arch uses StirlingLabs.MsQuic (msquic underneath, all-C# from project POV). Unity HS client uses `native/quic_client/` (quinn + rustls). VALERE91's "fairness" concern about native QUIC libs is settled: all three legs use native QUIC underneath, only gameplay code differs.
- **Wire format v4.** `InputCmd` quantized to **15 bytes** (was 25): `i16×1000` Move axes + `i16 angle×AngleScale` Aim (mirrors snapshot encoding). 40% bandwidth reduction on the input lane. `ProtocolVersion = 4`, ALPN bumped to **`dgsvshs/2`** everywhere (incidentally fixed Bevy server which was on a non-matching `dgs/v1`).
- **Snapshot delta + priority machinery on Arch.** `WorldStateHistory` ring (64 ticks), `RecipientSnapshotState` per slot (ConfirmedIds, TicksSinceLastSent, PendingSends with `MaxPending = MaxDeltaDepth*2` leak cap and reusable `_keysScratch`), `SnapshotPriority.SelectForDelta` (3 lanes: removed + spawn + animation), per-recipient delta-vs-full decision in `BroadcastSnapshot`. Bandwidth at 3000 enemies now ~100-300 B/tick instead of 18 KB (matches DGS).
- **Memory-leak fixes on the Unity DGS server (already merged in §7-style additions):**
  - `RecipientSnapshotState._pending` capped + reusable `_keysScratch`
  - `SnapshotPriority.SelectForDelta` `currentIdsScratch` + `baselineIndexByIdScratch` are caller-owned (was `new` every send → ~20 MB/sec of GC pressure)
  - `DedicatedServerMain` cached all `EntityManager.CreateEntityQuery` calls in fields (per-frame query allocation was leaking ~7 MB/sec with 0 clients connected at 500-1000 Hz idle frame rate)
  - `PlayerEnemyContactSystem` cached `_enemyCountQuery`
- **Per-server port layout** (one server per port so side-by-side trials don't collide):
  - Unity DGS → **7777**
  - C# Arch → **7778** (was 7777)
  - Rust Bevy → **4433**
  - BareBone → **7779** (was 7777)
- **Build mode switcher** now has `HS/Arch` and `HS/Bevy` submenus (DGS, BareBone unchanged). Selecting a mode also rewrites `ClientMain.Port` in the Client.unity scene file via SerializedObject so the Inspector matches the freshly selected target.
- **Compile-time flavors via publish scripts.** `scripts/publish_windows.ps1 -GodMode` and `scripts/publish_linux.sh --god-mode` pass `-p:GodModeDefault=true` → csproj appends `GODMODE_DEFAULT` define → `Program.cs` flips `Config.GodMode` to true by default. Output lands in `publish-godmode/` so normal and godmode binaries coexist. Pattern extensible — copy 3 layers for any new flavor.
- **Rust client diagnostic gated** behind `diag` Cargo feature (`cargo build --release --features diag` writes `dgsvshs_client.log` next to the host). Off by default — no per-send `format!` allocation overhead.
- **MicroVM build scripts for all three legs.** Bevy already had `rust/build_microvm_aarch64.sh`. Added matching scripts for Arch (`csharp_arch_server/build_microvm_aarch64.sh`) and Unity DGS (`DGSvsHS/build_microvm_aarch64.sh`). All three follow the same pattern: Alpine `linux-virt` aarch64 kernel + virtio_net modules from the kernel apk, custom `/init` shell script as PID 1, QEMU `virt` machine with user-mode networking + UDP port forward. Rootfs sourcing differs by leg (Rust = static musl ELF as `/init`; Arch = Docker buildx Alpine 3.21 musl + msquic; Unity = Docker buildx Debian bookworm-slim glibc + Unity Linux Server ARM64 build). Output of all three is `vmlinuz-virt + initramfs.cpio.gz` — not a qcow2/raw disk image. Suitable for direct QEMU boot or Firecracker; for Proxmox VE deployment use direct-kernel-boot via `args:` in `/etc/pve/qemu-server/VMID.conf` (see §10.1 below).

### 11.2 Current state per leg

| Leg | Lang | Server status | Wire | Notes |
|---|---|---|---|---|
| **DGS** | C# / Unity DOTS | Production-ready, all bug fixes merged | v4, NGO transport | Port 7777. Battle-tested wire codec (shared with all Unity-side builds). |
| **Arch** | C# / Arch / BepuPhysics | Production-ready, parity with DGS | v4, QUIC via StirlingLabs.MsQuic | Port 7778. Bepu kept as csproj dep but unused (gameplay hand-rolled, matches DOTS). All delta/ack/priority machinery in place. |
| **Bevy** | Rust / Bevy / Avian | Unverified — codec has v4 + tests pass, but plugin's snapshot send path **may not yet use** `SelectForDelta` (the function exists in codec.rs but I didn't audit plugin.rs to confirm). | v4, QUIC via quiche | Port 4433. ALPN was wrong (`dgs/v1` → `dgsvshs/2`). Build requires WSL (`boring-sys` needs NASM+CMake; doesn't compile on Windows). |

### 11.3 Next steps (prioritized)

1. **Audit `rust/gameplay/src/network/plugin.rs` snapshot send path.** Verify it composes per-recipient deltas with priority selection (the codec functions are there; the plugin orchestration may still be sending naïve fulls). If not, port the orchestration logic from `csharp_arch_server/Net/QuicServer.cs::BroadcastSnapshot`.
2. **Cross-stack QUIC interop smoke test.** quinn (client) ↔ quiche (Bevy) and quinn (client) ↔ msquic (Arch) both follow RFC 9000 + 9221 but minor differences (datagram size advertisement, stream FIN timing) occasionally surface. Have not seen the user run the Bevy server against the Unity HS client end-to-end yet.
3. **x86_64 variant of the microvm scripts.** Current scripts target aarch64 (matches the Rust one; works on Apple Silicon `hvf` and Ampere Linux `kvm`). Samuel's `pve01` Proxmox homelab is x86_64 — running the aarch64 microvms there falls back to QEMU TCG software emulation, which invalidates power-measurement trials. For pve01 deployment, port each script: swap `linux-musl-arm64` → `linux-musl-x64` (Arch), `aarch64` → `x86_64` Docker platform, `aarch64-unknown-linux-musl` → `x86_64-unknown-linux-musl` (Rust), `vmlinuz-virt` aarch64 apk → x86_64 apk, `qemu-system-aarch64` `virt`/`hvf` → `qemu-system-x86_64` `q35`/`kvm`, console arg `ttyAMA0` → `ttyS0`. Architectural choice deferred — keep aarch64 for Hetzner Ampere trials, add x86_64 for pve01 trials.
4. **Per-player RTT plumbing into `ctx.PlayerRttMs[]` on Arch.** Currently hardcoded 60 ms default → RewindResolve's view-time is uniform across all shooters. StirlingLabs.MsQuic doesn't expose RTT directly; need to call msquic's `QUIC_PARAM_CONN_STATISTICS` via the raw bindings each tick. Low impact unless measuring lag-comp effects in trials.
5. **NDJSON harness output format.** TrialRunner produces NDJSON on the client side; servers need to emit equivalent per-tick stats (ms/tick, RAM, RSS, enemy count) for power-correlation. The heartbeat lines are already in the apples-to-apples format (`SERVER STATS | RAM (RSS): X.XX MB | RAM (VM) : Y.YY MB | Uptime: Z.Zs | tick=N bodies=N state=...`) — needs an `--output trial.ndjson` flag too.
6. **Power measurement integration.** Out of code scope; wattmeter feeds a separate log that gets joined to server NDJSON by timestamp post-hoc.

### 11.4 Key files (jump-points for the next session)

- `csharp_arch_server/Program.cs` — main loop, lifecycle SM, history.Record + BroadcastSnapshot wiring
- `csharp_arch_server/Net/QuicServer.cs` — StirlingLabs.MsQuic wrap, BroadcastSnapshot (per-recipient delta-or-full), OnDatagramReceived (input parse + ack advance)
- `csharp_arch_server/Net/WireCodec.cs` — v4 wire codec, header + Full body + Delta body
- `csharp_arch_server/Net/SnapshotPriority.cs` — SelectForFull + SelectForDelta
- `csharp_arch_server/Server/RecipientSnapshotState.cs` — per-slot ack machinery with leak caps
- `csharp_arch_server/Server/WorldStateHistory.cs` — ring buffer
- `csharp_arch_server/scripts/publish_windows.ps1` / `publish_linux.sh` — supports `-GodMode` / `--god-mode`
- `DGSvsHS/Assets/_Game/Net/WireCodec.cs` — Unity-side shared codec (single source for DGS/HS client + DGS server)
- `DGSvsHS/Assets/_Game/Editor/BuildModeSwitcher.cs` — DGS / HS/Arch / HS/Bevy / BareBone menu + scene Port rewriter
- `DGSvsHS/Assets/_Game/Client/ClientMain.cs` — port default per `HS_TARGET_*` define
- `rust/gameplay/src/network/codec.rs` — Bevy wire codec, v4
- `rust/gameplay/src/network/plugin.rs` — Bevy QUIC plugin (snapshot send path **needs audit**)
- `native/quic_client/src/client.rs` — Rust QUIC client used by Unity HS via FFI; diag log gated behind `diag` feature
- `rust/build_microvm_aarch64.sh` — Bevy microvm (static musl `cli` as `/init`, no rootfs needed)
- `csharp_arch_server/build_microvm_aarch64.sh` — Arch microvm (Docker buildx → Alpine musl rootfs with msquic, .NET self-contained publish at `/opt/app`, port 7778)
- `DGSvsHS/build_microvm_aarch64.sh` — Unity DGS microvm (Docker buildx → Debian glibc rootfs, Unity ARM64 Linux Server build at `/opt/app`, port 7777, takes optional build-dir arg)

### 11.5 Bugs already fixed (don't re-introduce)

In addition to the §7 list, these were found this session:

- **Stream framing off-by-one.** Arch server was writing `length = 1 + payload.Length` (counted the msg_type byte) but the Rust client and the protocol spec define `length = payload.Length`. ServerWelcome would block the quinn reader forever waiting for byte 14 of a 13-byte payload. Fixed in `Net/WireCodec.cs::FrameStreamMessage` and the OnIncomingStream reader.
- **`LastProcessedInputTick = 0` hardcoded.** Without per-recipient ack tracking, the client never discarded pending inputs from its replay buffer; predicted player drifted ahead, snapped back on every snapshot, looked like "trying to go to the center." Fixed: `QuicServer` tracks `_highestInputTick[pid]`, stamped into each per-recipient snapshot.
- **`ReceiveDatagramsAsync = true` ThreadPool dispatch path.** StirlingLabs's async-dispatch route added latency + a potential drop point. Switched to sync dispatch on the msquic worker thread (`ReceiveDatagramsAsync = false`).
- **StirlingLabs.MsQuic 23.7.1 static initializer reads `Assembly.Location`.** Single-file publish defaults extract only native libs; managed assemblies stay in the bundle and `Assembly.Location` is empty → `new Uri("")` throws. Fixed by passing `-p:IncludeAllContentForSelfExtract=true` in publish scripts.
- **Bundled `libmsquic-openssl.so` is OpenSSL-1.1.** Ubuntu 24.04 has OpenSSL 3 only. Fixed via post-build symlink to apt-installed `libmsquic.so.2` (in csproj `DeployMsQuicNative` target).
- **MSBuild Copy of `runtimes/win-x64/native/msquic-openssl.dll` only valid for `dotnet build`, not `dotnet publish -r win-x64`.** Publish puts the .dll directly at `$(OutputPath)`. Fixed with `Exists()` condition on the Copy task.
- **XML comments cannot contain `--`.** csproj comment with `--god-mode` broke MSBuild project load. Use plain prose without double-hyphens inside `<!-- -->`.

---

## 12. Unreal/Mass leg — final architecture (2026-07)

The **fourth leg** (research contribution C): **Unreal Engine 5.7**, gameplay in **Mass ECS**, physics in **Chaos**. Headless dedicated-server target `UnrealvsHSServer`. QUIC via StirlingLabs/MsQuic (client uses quinn). **Port 7780.** Project root: `Unreal/UnrealvsHS/`. This section supersedes anything in §1 that implies only three legs.

### 12.1 Physics — raw Chaos particles owned by Mass (NO per-enemy actors)

This was the make-or-break work. Enemies were originally `AUvHSEnemyBody` **actors** (one `USphereComponent` each). At 15k enemies + ~1k spawns/sec, the AActor/component tax (transform sync, registration, spawn churn) pinned CPU at 100% — an unfair, non-ECS comparison against DGS (DOTS Physics) and Bevy (avian2d), which are ECS-native.

Fixed by moving physics to **raw Chaos rigid-body particles** in the solver, keyed to Mass fragments — no actors, no components:

- **`Server/UvHSEnemyBodyStore.{h,cpp}`** — owns Chaos particles by `int32` handle (freelist-backed). Enemies = **dynamic** spheres; players = **kinematic** spheres. `FSingleParticlePhysicsProxy` + `Solver->RegisterObject`, GT-particle API for force/readback. **Runs in METERS** (not cm) — matches DGS/avian and the shared constants exactly, so `EnemyDriveForce/EnemyMass/EnemyLinearDamping` give terminal velocity = `EnemySpeed`. Gravity off, rotation locked.
- **Fragment** `FUvHSEnemyChaosBodyFragment` holds an `int32 BodyHandle` (was a `TWeakObjectPtr<AUvHSEnemyBody>`).
- **Parity with DGS/Bevy (verified against the real code, not this doc):** dynamic enemies **block each other** (default collision filter / no layers) AND a **kinematic player body** they pile against. Bevy: `RigidBody::Dynamic + Collider::circle + LinearDamping + LockedAxes::ROTATION_LOCKED`, player `RigidBody::Kinematic`. DGS: `PhysicsMass.CreateDynamic + PhysicsDamping + PhysicsGravityFactor 0 + InverseInertia 0`, default filter.
- `AUvHSEnemyBody` is **deleted** — do not reintroduce it.
- **Gotcha:** the low-level Chaos headers use `std::numeric_limits::min()/max()`; the unity build leaks the Windows `min`/`max` macros in via the QUIC/Sockets TUs. `UvHSEnemyBodyStore.cpp` `#undef`s them before the Chaos includes. Don't remove that.

### 12.2 Physics backend toggle (A/B)

`AUvHSServerGameMode::bUseChaosPhysics` (`UPROPERTY`, default **true**; CLI `-UseChaos=true|false`), copied to `FSimContext::bUseChaosPhysics`:
- **true** → the Chaos particle backend above.
- **false** → hand-rolled O(N) force integration (`Sim::EnemySeek` writes velocity, `Sim::EnemyIntegrate` applies damping + position). **No bodies, no contacts** — a deliberate "engine physics vs. no physics" baseline for the paper. Kept on purpose.

`SimRunner::RunOneTick` branches: Chaos → `SyncChaosToFragments` (readback + drive kinematic player bodies); no-Chaos → `EnemyIntegrate`.

### 12.3 Bugs fixed (do not reintroduce)

- **`Sim::RewindResolve` was O(N²).** Lag-comp matched enemies between the two bracketing rewind frames with **nested linear scans** (floor×ceil). With the smoke bot firing every tick at 15k enemies that was ~13 billion ops/sec → 100% pin, mistaken for a "Chaos" cost. Now O(N) via a ceil `TMap<id,index>` + floor `TSet<id>`. (The snapshot-delta path in `Net/SnapshotPriority.cpp` was already O(N) — it uses `ScratchBaselineIndexById`.)

### 12.4 Editor smoke test — dev/Test only, compiled out of Shipping

`Sim::SimulatedClientInput` spawns N still players that **sweep-fire 360° every tick** so rounds auto-run 1→10 with no network client. Enabled by `-SimulatedClients=N` (or, in the editor, `bAutoSmokeTestInEditor` forces 1). Pairs with GodMode (default on). **All of it is `#if !UE_BUILD_SHIPPING`** (the editor-only `UPROPERTY` is `#if WITH_EDITORONLY_DATA` — UHT forbids `WITH_EDITOR` around a `UPROPERTY`). Shipping serves real clients only.

### 12.5 Tick cadence + a parity caveat

Server engine loop capped at **125 Hz** (`GEngine->SetMaxFPS(125)`); the sim steps at **62.5 Hz** via `FSimRunner`'s accumulator (`SimDt` 16 ms, `MaxStepsPerCall = 5`). Same 125/62.5 as DGS/Arch/Bevy. **Caveat:** Chaos physics ticks with the **UWorld physics tick (outer frame rate)**, not the fixed 62.5 Hz sim step — Bevy runs avian in `FixedUpdate` (inner 62.5). Forces accumulate across sub-steps and read back next tick. Not yet reconciled to a fixed physics step; flag if strict physics-step parity is needed.

### 12.6 Profiling + microvm

Trace only works in **Development/Test** builds — **Shipping strips CPU trace scopes** (an empty `.utrace` is the tell). Scripts (x86_64):
- `Unreal/UnrealvsHS/build_microvm_x86_64.sh` — plain Shipping/real-client microvm, no trace. Optional `SIM_CLIENTS` env removed (Shipping has no smoke test).
- `Unreal/UnrealvsHS/build_microvm_x86_64_trace.sh` — Test build, streams live to Unreal Insights via `-tracehost=<workstation>:<recorderPort>`. **Recorder port is version-specific** (UE 5.7 store shows Recorder 1981 / Store 1989, NOT 1980) — check Insights → Trace Store tooltip and set `TRACE_PORT`. Open TCP `<recorderPort>` inbound on the workstation firewall.

### 12.7 Key files

- `Server/UvHSServerGameMode.{h,cpp}` — lifecycle SM, `-QuicPort/-UseChaos/-GodMode/-SimulatedClients` parse, `SetMaxFPS`, `BodyStore.Shutdown` on EndPlay.
- `Server/UvHSEnemyBodyStore.{h,cpp}` — Chaos particle store (the physics core).
- `Server/SimContext.{h,cpp}` — POCO world state + `BodyStore`, spawn/despawn/destroy wiring.
- `Server/SimSystems.cpp` — sim steps (EnemySeek/Integrate, RewindResolve O(N), Sync, snapshot).
- `Server/SimRunner.cpp` — fixed-step accumulator + per-tick order (backend branch).
- `Mass/UvHSMassTypes.h` — enemy fragments.
- `Net/{WireCodec,SnapshotPriority,QuicServer}.*` — v4 wire, per-recipient delta.

### 12.8 Result

Unreal now runs **real Chaos rigid-body collision at 15k enemies** and sits in the pack with DGS and Bevy (all ≲45% CPU at round 10) — the AActor tax is gone. This leg is production-ready for trials.

---

End of `CLAUDE.md`. When in doubt, re-read §2 (gameplay model), §11 (C#/Rust legs), and §12 (Unreal leg). Most "should I do X?" questions are answered there.
