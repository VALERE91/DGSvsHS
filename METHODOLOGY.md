# DGSvsHS — Experimental Methodology

Companion document to the testing methodology of the DGSvsHS master's research project (UQAC / Département DIM). Describes the four game-server implementations under comparison, the shared client, the wire protocol that constrains all of them to bit-identical behaviour, and the harness used to drive reproducible trials.

This is the *system* side of the methodology — the *measurement* side (wattmeter setup, host configuration, trial schedules, statistical analysis) is documented separately and built on top of what is described here.

---

## 1. Research question and experimental design

**Question.** For a fixed gameplay workload run inside an ECS-based dedicated game server, how does total server-side energy consumption vary as a function of the underlying language / runtime / ECS stack / physics engine?

**Four implementations under test (independent variable).**

| Build | Language | Runtime | ECS | Enemy physics | Transport | Role in comparison |
|---|---|---|---|---|---|---|
| DGS | C# | Unity Dedicated Server (Mono/IL2CPP) | Unity DOTS / Entities | Unity DOTS Physics (`Unity.Physics`), impulse-driven, real contacts | UDP via Unity Netcode for GameObjects | "What studios ship today, ECS-flavoured" |
| Arch | C# | .NET 10 (`dotnet publish -r linux-musl-*` self-contained, single-file) | Arch ECS 2.0.0-beta | Hand-rolled kinematic (velocity-set + Euler integrate, **no engine, no contacts**; BepuPhysics referenced but dormant) | QUIC via StirlingLabs.MsQuic 23.7.1 | "What if we kept C# but escaped Unity" |
| Bevy | Rust | Cargo / `cargo zigbuild --target *-unknown-linux-musl` | Bevy ECS 0.18 (`MinimalPlugins`) | avian2d 0.5 (Rapier successor), force-driven, real contacts | QUIC via `quiche` 0.29 (BoringSSL) | "Native + Rust + heterogeneous orchestrator (PSDRC 2026-2027 proposal)" |
| Unreal | C++ | Unreal Engine 5.7 dedicated server (`UnrealvsHSServer`, headless) | Mass ECS | Chaos (raw solver rigid-body particles, no AActors), force-driven, real contacts | QUIC via native msquic (`libmsquic`) | "AAA engine + ECS + engine physics" |

**Why ECS for all four.** Pinning the architectural paradigm at ECS isolates language / runtime / physics-engine as the variables of interest. A MonoBehaviour-driven `List<EnemyState>` baseline against an ECS would conflate paradigm cost with runtime cost.

**Physics as a first-class variable.** Since the last revision, enemy movement moved from a hand-rolled kinematic model to **force-driven dynamic rigid bodies with real enemy↔enemy and enemy↔player contact resolution** in three of the four legs (DGS via DOTS Physics, Bevy via avian2d, Unreal via Chaos). This makes the physics engine an explicit independent variable and the contact solver a major share of per-tick CPU. **Arch is the exception**: it still runs the original kinematic model (set velocity toward nearest target, Euler-integrate, no collision), so its enemies interpenetrate freely. This is a known parity gap (see §3.5 and §10) — the intended target is force-based contacts on all four legs; Arch has not yet been ported. All four nonetheless reach the same 2.5 m/s terminal enemy speed (§3.5).

**Dependent variables.**

- Server-side power draw (W) measured on the wall via a hardware wattmeter on the server machine.
- Process-level cgroup metrics: CPU usage (% of one core), RAM (`memory.current`), TAP-interface RX/TX bytes/sec — captured at 20 Hz over SSH from the Proxmox host.
- Optional client-side derived metrics from snapshots and reconciliation events.

**Controls.**

- Identical gameplay (§3). Every gameplay decision affecting server CPU/memory cost lands in all four implementations with equivalent semantics; the wire format (§5) is the explicit contract.
- Identical client (§4) with deterministic autopilot inputs (§4.2). The client process is the same Unity binary; only the scripting-define flavour (DGS / HS/Arch / HS/Bevy) and the bot configuration differ per trial case. (The Unreal server speaks the same wire protocol; it is reached with an HS client build pointed at port 7780 — there is no dedicated build-mode entry for it yet, see §4.1.)
- Wattmeter measures the **server** machine; the client(s) run on a separate host (Hetzner box / Proxmox LXC). No client load contaminates the server measurement.
- All four servers run in microvms (§7) on the same Proxmox VMID with identical guest specs (CPU cores, RAM, NIC) so OS/firmware overhead is constant across cases.

---

## 2. Server implementations

All four servers implement the same state machine (§6.1), produce the same wire bytes (§5), and consume the same client inputs. What differs is the language, runtime, ECS storage strategy, physics engine, and QUIC stack.

### 2.1 DGS (Build 1) — Unity DOTS + NGO

**Location**: `DGSvsHS/Assets/_Game/Server/` (compiled when `WITH_DGS` define is active).

**Runtime**: Unity 6.3 LTS Dedicated Server platform. Built via Unity build profiles to a headless `.exe` (Windows / Linux). Mono or IL2CPP backend depending on profile (default IL2CPP for trials).

**ECS**: Unity DOTS / Entities. Components are unmanaged structs; systems are `ISystem` Burst-compiled jobs (`IJobEntity`, `IJobChunk`). Worlds are created manually (`new World("DGSvsHS.SimWorld")`) — the default bootstrap that auto-discovers all systems is bypassed so a headless server doesn't drag in unrelated systems.

**Sim loop**: `DedicatedServerMain.cs` is a `MonoBehaviour` that drives the DOTS `SimulationSystemGroup` from `Update()` via an accumulator:

```csharp
_tickAccumulator += Time.unscaledDeltaTime;
while (_tickAccumulator >= Constants.SimDt && safetyMaxTicks-- > 0)
{
    _tickAccumulator -= Constants.SimDt;
    _simGroup.Update();         // one sim tick
    ...
}
```

Outer Update is capped at 125 Hz (`Application.targetFrameRate = 125`); the sim sub-loop fires up to 5 times per Update frame to catch up if the outer rate falls behind. `Constants.SimDt = 0.016` so the effective sim tick rate is 62.5 Hz.

**Physics**: Unity DOTS Physics (`Unity.Physics`). Enemies are `PhysicsMass.CreateDynamic` sphere bodies (`SphereCollider`, `EnemyRadius`), gravity factor 0, inverse inertia 0 (rotation locked), linear damping `EnemyLinearDamping = 10`; the player is a kinematic sphere pinned to the authoritative input position each tick (`PinPlayerJob` zeroes its velocity). `EnemySeekSystem` applies a linear impulse toward the nearest active player (`EnemyDriveForce × SimDt`); the `PhysicsSystemGroup` (running inside `FixedStepSimulationSystemGroup`, inline per sim tick) integrates velocity→position and resolves enemy↔enemy + enemy↔player contacts with the default collision filter; `SyncPhysicsToPos2DSystem` writes `LocalTransform`/`PhysicsVelocity` back into `Position2D`/`Velocity2D` and clamps to the arena. `EnemyIntegrateSystem` is intentionally empty (the solver owns integration). Terminal enemy speed = `EnemyDriveForce / (EnemyMass × EnemyLinearDamping) = 2.5 m/s`.

**Transport**: Unity Netcode for GameObjects (NGO) configured as a transport only — no `NetworkBehaviour`, no `NetworkVariable`, no prefab replication. NGO's `CustomMessagingManager` is the carrier for raw bytes produced by the shared `WireCodec`. UnityTransport bound to UDP 7777.

**Snapshot path**: per tick, `SnapshotCaptureSystem` writes the world into a shared `Snapshot` POCO; `NgoNetworkServer.BroadcastSnapshot` composes a per-recipient Full or Delta (§5.4) and sends via NGO named messages.

**Rewind**: `RewindResolveSystem` is a managed system that runs inside the sim group; the rewind ring lives in unmanaged `NativeArray` storage shared with the Burst jobs.

**Build profile**: scene list = `Assets/Scenes/Server.unity` only. `WITH_DGS` define active. Output: `Build/<date>/Server/DGSvsHS.exe`.

### 2.2 Arch (Build 2) — C# / Arch ECS + msquic

**Location**: `csharp_arch_server/`. Standalone .NET 10 console app, no Unity runtime.

**Runtime**: `dotnet publish -r linux-musl-{arm64,x64} --self-contained -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true`. Server GC, concurrent GC, default heap sizing. Distributed as a single ~30 MB self-extracting binary deployed inside an Alpine Linux microvm.

**ECS**: [Arch](https://github.com/genaray/Arch) 2.0.0-beta. Archetype-based, chunked-storage, pure-C#. Components are blittable structs (`EnemyTag`, `EnemyId`, `Position2D`, `Velocity2D`, etc.). Systems are static methods that drive `world.Query(in QueryDescription, lambda)` callbacks.

**Sim loop**: `Program.cs` runs a tight busy-wait main loop using `Stopwatch.GetTimestamp()`, clocked at a **125 Hz outer period (8 ms)** with the sim firing on every second outer iteration (62.5 Hz), matching the 2:1 outer:sim ratio of the other legs (§6.4):

```csharp
var outerPeriodTicks = Stopwatch.Frequency * Constants.SimTickMs / 1000 / 2; // 8 ms
while (now < nextTickTime) {
    var remainingMs = (nextTickTime - now) * 1000.0 / Stopwatch.Frequency;
    if (remainingMs > 2) Thread.Sleep(1);
    now = Stopwatch.GetTimestamp();
}
nextTickTime += outerPeriodTicks;
quic?.PollEvents();                          // every 8 ms
if ((++outerCounter & 1UL) == 0) {           // sim + broadcast every 16 ms
    // TickAdvance → … → BroadcastSnapshot
}
```

Network polling runs every 8 ms; the sim block (`TickAdvance → BroadcastSnapshot`) runs every other outer iteration, so the sim is authoritatively 62.5 Hz with no accumulator drift.

**Physics**: **hand-rolled kinematic — no engine, no contacts.** `EnemySeek.Run` sets each enemy's `Velocity2D` directly to `dir × EnemySpeed` toward the nearest active player; `EnemyIntegrate.Run` advances `Position2D += Velocity2D × SimDt`. There is no collision resolution, so enemies interpenetrate each other and the player freely. `BepuPhysics v2` is a referenced dependency but dormant. This makes Arch the one leg **without** real contact physics — a structural workload asymmetry against DGS/Bevy/Unreal that is called out as a threat to validity (§3.5, §10). Terminal enemy speed is `EnemySpeed = 2.5 m/s` directly (the force legs converge to the same value via `EnemyDriveForce / (EnemyMass × EnemyLinearDamping)`).

**Transport**: QUIC via [StirlingLabs.MsQuic](https://github.com/StirlingLabs/Quic.NET) 23.7.1 (managed wrapper over msquic 2.x). ALPN `dgsvshs/2`. Listens on UDP 7778. Inbound `InputCmd`s arrive as datagrams; `ClientHello` / `ServerWelcome` exchanged on a bidirectional control stream with explicit length-prefix framing (§5.1).

**Snapshot path**: `SnapshotCapture.Run` writes the world into a shared `Snapshot`; `WorldStateHistory.Record` deep-copies into a 64-slot ring; `QuicServer.BroadcastSnapshot` composes per-recipient deltas (§5.4) using the `SnapshotPriority.Select*` primitives, encoded into a fresh `byte[]` per send and handed to `slot.Connection.SendDatagram`.

**Rewind**: `RewindResolve.cs` mirrors the DGS implementation 1:1; ring storage is plain `ushort[]` + `Vector2[]` arrays sized at `SnapshotHistoryTicks × MaxEnemies`, pre-allocated at startup.

**Dependencies**: `Arch 2.0.0-beta`, `BepuPhysics v2.5.0-beta.24` (referenced for future broadphase use, currently dormant — hand-rolled spatial grid), `StirlingLabs.MsQuic 23.7.1`. msquic native library is symlinked to apt-installed `libmsquic.so.2` in the microvm to bypass the bundled OpenSSL-1.1 dependency.

### 2.3 Bevy (Build 3) — Rust / Bevy ECS + quiche

**Location**: `rust/`. Two crates: `gameplay` (library) and `cli` (entry point).

**Runtime**: Rust edition 2024, `cargo zigbuild --target {aarch64,x86_64}-unknown-linux-musl --release`. `mimalloc` as the global allocator. Distributed as a static-musl ELF (~3 MB) deployed inside an Alpine Linux microvm.

**ECS**: [Bevy](https://bevyengine.org) 0.18 with `default-features = false` + `bevy_log`. `MinimalPlugins` for headless operation. Components are tagged Rust structs (`Enemy`, `Pos2D`, `Vel2D`, `EnemyId(u16)`). Systems are plain functions; the sim sub-step order is enforced via a typed `SimSet` enum and `.chain()`.

**Sim loop**: `ScheduleRunnerPlugin::run_loop(...)` ticks the outer Update loop at 125 Hz; sim systems are registered in `FixedUpdate` with `Time::<Fixed>::from_hz(62.5)` and `run_if(sim_running)`, so Bevy's fixed-timestep accumulator fires one sim tick every two outer Updates (integer 2:1 ratio → no jitter). See §6.4.

**Physics**: [avian2d](https://github.com/Jondolf/avian2d) 0.5 (`default-features = false`, `2d` + `f32` + `parry-f32`). Enemies are `RigidBody::Dynamic` + `Collider::circle(ENEMY_RADIUS)` + `LinearDamping(ENEMY_LINEAR_DAMPING)` with rotation locked; the player is `RigidBody::Kinematic` driven to the input position. `enemy_seek` applies `forces.apply_force(dir × ENEMY_DRIVE_FORCE)` toward the nearest active player. avian's `PhysicsSystems::StepSimulation` runs inline inside `FixedUpdate` (scheduled in `SimSet` between `EnemySeek` and `SyncFromPhysics`), integrating and resolving enemy↔enemy + enemy↔player contacts; `sync_physics_to_pos2d` reads `Position`/`LinearVelocity` back into the POCO world and clamps to the arena. Terminal enemy speed = 2.5 m/s (same drive/damping identity as DGS).

**Transport**: QUIC via [`quiche`](https://github.com/cloudflare/quiche) 0.29 (Cloudflare, BoringSSL underneath). ALPN `dgsvshs/2`. Listens on UDP 4433. Same datagram-for-Input, stream-for-ClientHello/ServerWelcome split as the Arch server, with the same length-prefix stream framing.

**Snapshot path**: `snapshot_capture` system writes the world into `SnapshotScratch`; `record_history` deep-copies into the `WorldStateHistory` ring; `broadcast_snapshot` composes per-recipient Full/Delta via `SnapshotPriority::select_for_*` and emits `NetMsgOut` events that the QUIC plugin sends in `PostUpdate`.

**Rewind**: `RewindRing` is a `Vec<RewindFrame>` of `(tick, Vec<(EnemyId, Vec2)>)` records, capped at `SNAPSHOT_HISTORY_TICKS`.

**Dependencies**: `bevy = 0.18.1`, `quiche = 0.29`, `mimalloc = 0.1.52`, `libc = 0.2`. `boring-sys` (BoringSSL) compiled from source via `quiche` — requires Zig as the cross-linker for the musl target.

### 2.4 Unreal (Build 4) — C++ / Mass ECS + Chaos

**Location**: `Unreal/UnrealvsHS/`. Headless dedicated-server target `UnrealvsHSServer`; project module `UnrealvsHS`.

**Runtime**: Unreal Engine 5.7 (source build). Cooked dedicated-server build (Shipping for trials; Development/Test for profiling — Shipping strips CPU trace scopes). Distributed inside a Debian-glibc microvm (Unreal's Linux server needs glibc).

**ECS**: [Mass](https://dev.epicgames.com/documentation/en-us/unreal-engine/mass-entity-in-unreal-engine) (`MassEntity` + `MassGameplay` plugins). Enemies are Mass entities (`FUvHSEnemyTag` + Id/Position/Velocity/ChaosBody fragments); the POCO world state (`FSimContext`) owns players, round state, RNG, and the rewind ring. There is **no per-enemy AActor** — the earlier actor-per-enemy approach pinned CPU at 15k+ enemies and was removed.

**Sim loop**: `AUvHSServerGameMode::Tick` drives `FSimRunner::AdvanceWallTime`, a fixed-step accumulator (`SimDt = 16 ms`, up to `MaxStepsPerCall = 5`). The engine frame is capped at 125 Hz (`GEngine->SetMaxFPS(125)`); the sim steps at 62.5 Hz. See §6.4.

**Physics**: **Chaos**, driven from Mass — raw solver rigid-body particles, no `UPrimitiveComponent`s. `FUvHSEnemyBodyStore` owns `FSingleParticlePhysicsProxy` bodies by integer handle: enemies are **dynamic** spheres (`EnemyRadius`, mass 1, gravity off, rotation locked, linear ether drag = `EnemyLinearDamping`), players are **kinematic** spheres teleported to the authoritative position each tick. `EnemySeek` calls `AddForce(dir × EnemyDriveForce)`; the Chaos solver integrates and resolves enemy↔enemy + enemy↔player contacts. The store runs in **meters** (matching the shared constants). Sim-collision is enabled per-shape with an all-ones filter; query collision is off (hitscan is manual). Terminal enemy speed = 2.5 m/s.
  - **Backend A/B toggle**: `-UseChaos=true|false` (default true). `false` swaps in the hand-rolled `EnemyIntegrate` (velocity + damping + Euler, no bodies/contacts) as an "engine physics vs. no physics" baseline for the paper.
  - **Contact-solver budget**: Chaos iteration counts are pinned in `Config/DefaultEngine.ini` `[ConsoleVariables]` to `p.Chaos.Solver.Iterations.Position=2` / `Velocity=1` (engine defaults 8 / 2). The enemies are non-stacking damped circles that only need soft push-apart, so the reduced budget cuts contact-solve CPU ~4× with essentially identical pile behaviour and sits closer to the low-iteration XPBD budgets avian2d/DOTS Physics run.
  - **Parity caveat**: Chaos ticks with the UWorld physics tick (async fixed step, `AsyncFixedTimeStepSize = 0.016`), not strictly inline inside the 62.5 Hz sim sub-step. Results are read back into fragments at the **top** of the next sim tick (`SyncChaosToFragments`), so there is a one-tick coupling that the inline-stepping legs (DGS/Bevy) do not have (§6.4).

**Transport**: QUIC via native **msquic** (`libmsquic.so.2`, `dlopen`'d) on both server (`FQuicServer`) and the Unreal-side client shim. ALPN `dgsvshs/2`. Assigned port **7780** (via `-QuicPort=7780`; the compiled default in `UvHSServerGameMode.h` is still 7777, harmless because each leg runs in its own microvm). Same datagram-for-Input / stream-for-ClientHello+ServerWelcome split and length-prefix framing as the other QUIC legs.

**Snapshot path**: `Net/WireCodec` + `Net/SnapshotPriority` mirror the shared v4 codec and per-recipient delta machinery (§5.4). `FQuicServer::BroadcastSnapshot` composes per-recipient Full/Delta against a `WorldStateHistory` ring.

**Rewind**: `Sim::RewindResolve` implements the same bracketing-frame interpolation, O(N) via a ceil id→index map + floor id set (an earlier O(N²) nested scan was the round-10 CPU blowup and has been fixed).

**Dependencies**: UE 5.7 (`MassEntity`, `MassGameplay`, `StateTree`, `GameplayStateTree`, `StructUtils` plugins) + native msquic.

---

## 3. Gameplay model (locked across all four servers)

Identical behaviour is enforced by:
- Shared constants in `Gameplay/Constants.cs` (DGS / Arch) and `rust/gameplay/src/game/constants.rs` (Bevy) with values pinned to the same numbers.
- The wire format (§5) as a runtime check (every snapshot embeds `sim_tick_ms` and `snapshot_every_n_ticks` so any drift is a hard fail).
- The sub-step order contract (§6.2).

### 3.1 Match rules

- 2D top-down survivor-shooter. Up to **4 players** cooperate, no PvP.
- **10 rounds** per match, escalating enemy counts: `round(BaseEnemiesPerRound × EnemyScalingPerRound^(round−1))`, bounded by the `MaxEnemies` storage ceiling.
  - `BaseEnemiesPerRound = 1700`, `EnemyScalingPerRound = 1.4`, `MaxEnemies = 1_000_000` (a simulation ceiling, not a per-round cap).
  - Round 1 = 1700, round 10 = `round(1700 × 1.4⁹) ≈ 35124` enemies (well under the ceiling — the escalation is not clamped in practice).
- Spawn pacing: enemies for each round are spawned uniformly across `RoundSpawnWindowSec = 18 s` at the arena rim.
- Inter-round delay: 3 s. Total active round = 18 s spawn + however long it takes to clear remaining enemies.
- Server is **idle** (no enemies, round 0) when no client is connected. First client connect → 3 s `InterRound` countdown → round 1.
- Last client disconnect → server reseeds RNG, wipes state, returns to idle. Reproducible trials.

### 3.2 Controls and combat

- WASD → world-space movement at `PlayerSpeed = 6 m/s`.
- Mouse → twin-stick aim (vector to mouse-world position).
- Left mouse → continuous fire with per-fire cooldown `PlayerFireCooldownSec = 0.12 s`.
- **Hitscan piercing laser** — no projectile entities. Beam segment of length `BulletMaxRange = 50 m` and radius `BeamRadius = 0.2 m`. Kills every enemy in its path (segment-vs-circle with `EnemyRadius + BeamRadius` clearance).
- **Lag compensation**: bracketing-frame rewind (§5.7) with `InterpolationBufferMs = 100`.
- Fire prediction is client-side and zero-delay; the server confirmation event is filtered out for the local player to avoid double-rendering.

### 3.3 Disable mechanic

- Player touches enemy → `DisableTimer = DisableDurationSec = 10 s`. Player stays alive, can move and aim, **cannot fire**, is **invulnerable** during disable.
- All connected players simultaneously disabled → **team wipe** → server resets to round 1, clears all enemies and disable timers. (No "Defeat" phase in PvE.)

### 3.4 Arena

- Disc, `ArenaRadius = 25 m`. Players clamped to `r ≤ ArenaRadius − PlayerRadius`.

### 3.5 Enemy physics model

Enemies seek the nearest **alive, non-disabled** player and pile up on contact. The steering + collision model is shared by value through the constants (`EnemyMass = 1`, `EnemyLinearDamping = 10`, `EnemyDriveForce = EnemySpeed × EnemyLinearDamping = 25`, `EnemyRadius = 0.35`, `PlayerRadius = 0.4`). The **terminal enemy speed is 2.5 m/s** on every leg — the three force-driven legs reach it as `EnemyDriveForce / (EnemyMass × EnemyLinearDamping)`, Arch sets it directly.

| Leg | Drive | Integration + contacts | Engine | Steps physics… |
|---|---|---|---|---|
| DGS | linear impulse `EnemyDriveForce × SimDt` | integrate + resolve enemy↔enemy / enemy↔player contacts | Unity DOTS Physics | inline (`PhysicsSystemGroup` in `FixedStepSimulationSystemGroup`) |
| Bevy | `apply_force(EnemyDriveForce)` | integrate + resolve contacts | avian2d 0.5 | inline (`PhysicsSystems::StepSimulation` in `FixedUpdate`) |
| Unreal | `AddForce(EnemyDriveForce)` | integrate + resolve contacts | Chaos (raw particles) | async UWorld physics tick, read back next sim tick |
| **Arch** | **set `Velocity2D = dir × EnemySpeed`** | **Euler `pos += vel × SimDt`, no contacts** | **none (hand-rolled)** | inline (`EnemyIntegrate`) |

**Consequence for the experiment.** In DGS / Bevy / Unreal, the contact solver over ~35k mutually-colliding bodies at round 10 is a dominant, engine-specific share of server CPU — which is precisely the signal the paper measures. **Arch runs no contact solver at all**, so its enemies overlap freely and its round-10 CPU is not comparable on a like-for-like physics basis. Treat Arch either as a deliberate "ECS runtime without engine physics" data point or as a pending port; do not read an Arch-vs-DGS CPU delta as purely a language/runtime effect. This is the single largest cross-leg parity gap and is repeated in §10. (Unreal's `-UseChaos=false` mode provides an on-engine analogue of the same "no-contacts" baseline for a within-leg comparison.)

---

## 4. Client

### 4.1 The Unity binary

Single Unity 6.3 LTS project at `DGSvsHS/Assets/_Game/Client/`. **One binary** serves all four server flavours (the two QUIC sub-targets already cover Arch, Bevy, and — by port override — Unreal); only the scripting defines change which transport / port is compiled in:

| Define | Transport | Default port | Server target |
|---|---|---|---|
| `WITH_DGS` | NGO over UnityTransport (UDP) | 7777 | DGS |
| `WITH_HS` + `HS_TARGET_ARCH` | Native QUIC via `dgsvshs_socket.dll` (P/Invoke) | 7778 | Arch |
| `WITH_HS` + `HS_TARGET_BEVY` | Native QUIC via `dgsvshs_socket.dll` (P/Invoke) | 4433 | Bevy |
| `WITH_HS` (either sub-target) with `Port` overridden to 7780 | Native QUIC via `dgsvshs_socket.dll` (P/Invoke) | 7780 | Unreal |

The Editor menu `DGSvsHS/Build Mode/*` (`BuildModeSwitcher.cs`) flips the active set and rewrites `ClientMain.Port` in the scene file so a build picked from the build profile UI Just Works. **Note:** the switcher currently exposes DGS / HS-Arch / HS-Bevy only — there is no dedicated `HS_TARGET_UNREAL` entry yet. The Unreal server speaks the identical v4 wire protocol over the same native-QUIC client (ALPN `dgsvshs/2`), so it is reached today by taking any `WITH_HS` build and pointing `ClientMain.Port` at 7780. Adding a first-class Unreal build mode is a pending client-side task.

**Client architecture** (`ClientMain.cs`):
- Pure `MonoBehaviour` orchestrator.
- `ClientSimulation.cs` handles prediction (zero-delay local input application) and reconciliation (snap-back from server snapshots).
- `EnemyCorrector.cs` smooths remote-entity positions via a critically-damped spring (`K`, `C` constants, snap fallback at `EnemyCorrectionSnapDistance`).
- `PlayerInputReader.cs` samples WASD + mouse + LMB via Unity's Input System.
- The native QUIC client (HS modes) is a Rust dylib (`native/quic_client/`, source in this repo) built with `quinn` + `rustls`. P/Invoked via `QuicNetworkClient.cs`.

### 4.2 AutoPilot (deterministic bot driver)

`Assets/_Game/Client/AutoPilot.cs` is a pure function of `(client_tick, bot_id, seed)` — zero snapshot inspection, zero wall-clock dependence, zero non-determinism. Same `(bot_id, seed)` always emits the same per-tick `InputCmd` stream regardless of server, machine, or network jitter.

Per-bot RNG drawn from `DeterministicRng::from_seed(seed XOR (0xA5A5A5A5 + bot_id))` produces seven scalars at construction:
- `orbit_radius ∈ [0.4·ArenaR, 0.75·ArenaR]`
- `orbit_angular_speed ∈ [0.5, 1.2] rad/s`
- `orbit_phase ∈ [0, 2π)`
- `aim_angular_speed ∈ [2.0, 4.5] rad/s`
- `aim_phase ∈ [0, 2π)`
- `fire_on_ticks ∈ [30, 60]` (~480–960 ms)
- `fire_off_ticks ∈ [6, 18]` (~100–290 ms)

Per tick:
- **Move** = unit vector from current predicted position toward the next point on the orbit circle.
- **Aim** = `(cos(aim_phase + aim_angular_speed × tick × SimDt), sin(...))`. Pure rotation, ignores enemy positions.
- **Fire** = `(client_tick % (fire_on + fire_off)) < fire_on`. Server-side cooldown (`PlayerFireCooldownSec = 0.12 s`) further rate-limits.

Activated by `--bot-id N` on the client process command line (parsed by `ClientMain.ApplyCommandLineArgs`). Without `--bot-id`, falls back to keyboard/mouse via `PlayerInputReader`.

**Why pure-function aim**: snapshot-driven aim ("nearest enemy") couples the bot's fire pattern to network arrival jitter, which makes two runs of the same trial emit different `FireEvent` timestamps. Pure-function aim removes that confounding variable — the workload imposed on the server is identical bit-for-bit across runs and across servers.

---

## 5. Wire protocol

Specified canonically in `DGSvsHS/Assets/_Game/Net/WireFormat.md` (v4). All four servers and the client implement *that* spec — engines disagree on internal representation but never on the bytes that cross the wire. Summary here for the paper; consult `WireFormat.md` for byte-level field layouts.

### 5.1 Framing

| Direction | Transport | Framing |
|---|---|---|
| C → S: `Input` | Datagram | `[msg_type:1][payload:N]` — length implicit from datagram |
| C → S: `ClientHello` | Stream | `[length:u32 LE][msg_type:1][payload:N]` — length is payload size only |
| S → C: `Snapshot` | Datagram | `[msg_type:1][payload:N]` |
| S → C: `ServerWelcome` | Stream | `[length:u32 LE][msg_type:1][payload:N]` |

QUIC builds (Arch, Bevy, Unreal) split reliable control messages onto streams and unreliable game messages onto datagrams. NGO build (DGS) uses named messages atomically — no separate stream/datagram channels. The bytes inside `payload` are byte-for-byte identical across all four.

### 5.2 Message types

- `0x01 ClientHello` — protocol version + capabilities. Reliable.
- `0x02 ServerWelcome` — assigns player id, echoes config. Reliable.
- `0x10 Input` — `InputCmd` batch (1–4 commands, redundancy for unreliable delivery). 15 bytes per `InputCmd`.
- `0x20 Snapshot` — Full or Delta. ~24 byte header + variable body.
- `0xF0 Disconnect` — reason byte. Reliable.

### 5.3 Quantization

- Position: `i16 = round(meters × 1000)` → 1 mm precision, ±32.767 m range.
- Aim angle: `i16 = round(radians × 10430)` → ~0.0055° precision.
- Disable timer: `u16 = round(seconds × 62.5)` → tick-precision, max 1048 s.

### 5.4 Per-recipient delta encoding

Each server maintains per-recipient state:
- `last_acked_server_tick` (monotonic, pulled from incoming `InputCmd.last_acked_server_tick`)
- `confirmed_ids: Set<u16>` (enemy ids the client has acknowledged seeing)
- `ticks_since_last_sent: Map<u16, u16>` (staleness counter, saturates at u16::MAX)
- `pending_sends: Queue<PendingSend>` (unacked snapshots; capped at `MaxDeltaDepth × 2 = 64` entries)

Snapshot composition per recipient per tick:

1. **Decide Full vs Delta**: Full if `acked == 0` OR `current_tick - acked > MaxDeltaDepth (32)` OR baseline snapshot no longer in history ring; else Delta against `WorldStateHistory[acked]`.
2. **Budget**: `SnapshotByteBudget = 1200 B` minus header/player/fire overhead = enemy section budget.
3. **Removed lane** (Delta only): iterate `confirmed_ids` − current enemies → emit as `removed_enemy_ids` (2 B each). Pack first since they're tiny and dropping them risks ghost enemies.
4. **Spawn lane**: current enemies − `confirmed_ids` → sorted by distance² to recipient anchor → pack up to `MaxSpawnsPerSnapshot = 30` and remaining budget into `new_enemies` (6 B each).
5. **Animation lane**: enemies in both confirmed and baseline → scored by `distance − StalenessWeight × ticks_since_last_sent` → packed into `changed_enemies` (6 B each) skipping any whose quantized position equals the baseline.
6. **Bookkeeping**: append `PendingSend(tick, is_full, included, removed)` to the queue. When the client's next `InputCmd` acks a tick ≥ this one, retire the entry and update `confirmed_ids` accordingly.

This is the contract every server implements identically. The `confirmed_ids` set is what carries removed-enemy information across delta boundaries (§5.4 of `WireFormat.md`).

### 5.5 Rewind contract

Every server implements bracketing-frame interpolation for hitscan resolution (§7 of `WireFormat.md`). Summary:

```
view_tick_f = server_tick − (RTT/2000) × 62.5 − (100/1000) × 62.5
```

Bracketing-frame lookup against a `SnapshotHistoryTicks = 64`-deep ring (one frame per tick). Interpolated enemy positions = `lerp(floor.pos, ceil.pos, alpha)`. Beam vs interpolated set produces kills, applied to the **current** world by enemy id ("shot around the corner" tradeoff for fairness against latency).

---

## 6. Determinism

### 6.1 Server lifecycle state machine

Every server is `Booting → Idle → Running → Resetting → Idle → … → ShuttingDown`. The sim sub-steps run **only** in `Running`. `Resetting` is a single-tick transient that destroys all enemies, reseeds the RNG to the trial seed, and clears the rewind ring. All four implementations emit `state: X → Y tick=N` log lines so trial timelines align across builds.

### 6.2 Sim sub-step order (per tick, identical across implementations)

1. `Tick++`; clear per-tick fire-event accumulator.
2. **Round director**: phase transitions, enemy spawn pacing. Spawn placement consumes one `next_range(0, 2π)` RNG draw per enemy — order must match.
3. **Drain inputs**: apply latest per-player move/aim, decay cooldown/disable timer, queue `Fire` commands. Player position is authoritative (input-integrated), and the kinematic player body is driven to it.
4. **Rewind resolve**: hitscan against bracketed history ring, kill enemies.
5. **Enemy AI (seek + drive)**: per enemy, compute the unit vector to the nearest alive, non-disabled player, then apply the drive toward it. Force legs apply `EnemyDriveForce` (DGS: linear impulse `× SimDt`; Bevy/Unreal: `apply_force`/`AddForce`); Arch sets `Velocity2D = dir × EnemySpeed` directly.
6. **Physics step (integrate + resolve contacts)**: the engine integrates velocity→position under linear damping and resolves enemy↔enemy + enemy↔player contacts, then results are read back into the POCO world with an arena clamp. DGS/Bevy step **inline** here; Unreal reads back last tick's async Chaos result at the **top** of the tick (one-tick coupling, §6.4); Arch does a plain Euler `position += velocity × SimDt` with **no contacts**.
7. **Player-enemy contact**: alive non-disabled player within `(PlayerKillRadius + EnemyRadius)` of any enemy → `DisableTimer = DisableDurationSec` (skipped in God Mode).
8. **Rewind record**: write post-step world into ring.
9. **Snapshot compose + broadcast**.

Because contact resolution reorders per-body float work internally, the physics-engine legs are **not** guaranteed bit-identical enemy positions to each other; the wire contract only requires agreement to the 1 mm quantization grain (§9). Arch, being contact-free, is fully deterministic on its own but diverges from the engine legs by the accumulated overlap it never resolves.

### 6.3 RNG

xoroshiro128+ with SplitMix64 seed expansion. Same `u64` seed → bit-identical output stream across all four implementations.

### 6.4 Frame-rate cadence

All four servers run an **exact 2:1 outer:sim ratio**: 125 Hz outer (8 ms period) / 62.5 Hz sim (16 ms period). Per-Update overhead is therefore a constant across the comparison, not a per-server confound.

| Server | Outer loop rate | Sim rate | Mechanism |
|---|---|---|---|
| DGS | 125 Hz | 62.5 Hz | `Application.targetFrameRate = 125` + accumulator in `DriveSim()`: each Update adds 8 ms, sim fires every 2 Updates exactly. |
| Arch | 125 Hz | 62.5 Hz | `Stopwatch` busy-wait + 1 ms `Sleep` at 8 ms period; sim block gated on `(outerCounter & 1) == 0` so it runs every other iteration. |
| Bevy | 125 Hz | 62.5 Hz | `ScheduleRunnerPlugin::run_loop(1/125)` outer; sim systems in `FixedUpdate` with `Time::<Fixed>::from_hz(62.5)` — Bevy's accumulator handles the integer 2:1 ratio deterministically (no jitter; jitter only appears with non-integer ratios). |
| Unreal | 125 Hz | 62.5 Hz | `GEngine->SetMaxFPS(125)` engine frame + `FSimRunner` accumulator (`SimDt = 16 ms`, `MaxStepsPerCall = 5`) steps the sim at 62.5 Hz. **Caveat:** the Chaos *physics* tick runs on the UWorld async fixed step (`AsyncFixedTimeStepSize = 0.016`), not inside the sim sub-step, so contact results lag by one sim tick (read back at top-of-tick). Not yet reconciled to a strictly inline physics step. |

In all four: network polling runs every 8 ms (snapshots and inputs get drained twice as often as the sim ticks), reducing worst-case input-arrival latency from 16 ms to 8 ms while keeping the sim authoritatively at exactly 62.5 Hz.

---

## 7. Deployment — microvms

All four servers ship as Linux microvms built via Docker buildx (Alpine/musl for the two static legs, Debian/glibc for the two engine legs). Two architectures supported (`*_aarch64.sh` and `*_x86_64.sh`); x86_64 also produces a hybrid BIOS+UEFI bootable ISO via `grub-mkrescue` for Proxmox deployment. (aarch64 microvms fall back to slow TCG emulation on the x86_64 `pve01` host — use x86_64 for power trials there; keep aarch64 for the Hetzner Ampere box.)

| Server | Script | Base image | Binary | Static IP (default) | Port |
|---|---|---|---|---|---|
| DGS | `DGSvsHS/build_microvm_*.sh` | `debian:bookworm-slim` (glibc, Unity needs it) | Unity Linux Server build | 192.168.0.205/24 | 7777 |
| Arch | `csharp_arch_server/build_microvm_*.sh` | `alpine:3.21` (musl) + apt'd libmsquic | `dotnet publish` self-contained | 192.168.0.205/24 | 7778 |
| Bevy | `rust/build_microvm_*.sh` | `alpine:3.21` (musl) | `cargo zigbuild` static-musl | 192.168.0.205/24 | 4433 |
| Unreal | `Unreal/UnrealvsHS/build_microvm_x86_64*.sh` (+ `_aarch64`) | `debian:bookworm-slim` (glibc, UE needs it) + libmsquic | UE 5.7 Linux dedicated-server build | 192.168.0.205/24 | 7780 |

Each microvm boots a custom `/init` shell script that:
1. Mounts `/dev`, `/proc`, `/sys`, `/tmp`
2. `insmod`s `failover`, `net_failover`, `virtio_net`
3. Configures static IP on eth0
4. Launches the server binary in the background, redirecting stdout to `/tmp/{arch,unity,rust,unreal}.log`
5. Spawns `setsid /bin/sh -i` on the serial console (operator can press Enter to get a shell, `tail -f /tmp/<flavour>.log` to watch the server live)

For Proxmox deployment: upload the ISO, attach as CD/DVD, set network bridge to `vmbr0`, boot. The VM's MAC starts with `bc:24:11` (Proxmox OUI) and gets `192.168.0.205` on the LAN.

---

## 8. Test harness

Two Python scripts under `test_harness/`:

### 8.1 `record_run.py` — telemetry recorder

SSHes into the Proxmox host using credentials in `.env` (`PROXMOX_HOST`, `PROXMOX_USER`, `PROXMOX_PASSWORD`), injects a small Python sampler over stdin that reads cgroup files for the target VMID at a configurable rate (default 20 Hz), and streams the resulting JSON lines back to the local machine, written to `results/<case>.jsonl`.

Per-sample schema:
```json
{"t": 1781753980.711, "c": 11.4, "m": 882270208, "rx": 26033.62, "tx": 24580.61}
```
- `t` — Unix wall-clock timestamp (recorder side)
- `c` — guest CPU usage in % of one core (delta of cgroup `cpu.stat.usage_usec`)
- `m` — guest cgroup `memory.current` in bytes
- `rx`, `tx` — guest tap interface RX/TX bytes per second

The script runs entirely on the Proxmox host inside a `python3 -` subprocess; the SSH channel is the only network egress. Stopping the script (Ctrl+C or `SIGINT`) triggers a `finally` block that cleanly closes the SSH connection and flushes the output file.

### 8.2 `run_case.py` — single-case orchestrator

Per invocation:

1. Starts `record_run.py` as a subprocess (in its own process group on Windows so it can receive a clean Ctrl+Break later).
2. Sleeps 3 s for the SSH stream to settle and the idle baseline to land in the .jsonl.
3. Launches N (default 2, max 4) Unity client `.exe` processes — flavor-specific path from `CLIENT_EXE_{DGS,ARCH,BEVY}` env var, each with `--bot-id i`. The client's `ClientMain.ApplyCommandLineArgs` then auto-attaches `AutoPilot(i, seed)` (§4.2). (For the Unreal leg, reuse a `WITH_HS` client build with its port set to 7780 — there is no `CLIENT_EXE_UNREAL` flavor yet; the wire protocol is identical so any HS client works.)
4. Waits a configurable active phase (default 270 s = 4 min 30 s).
5. Terminates client processes (`process.terminate()`).
6. Holds a configurable cooldown (default 30 s) so the recorder captures the post-disconnect idle drop-off.
7. Sends Ctrl+Break to the recorder, which runs its SSH-cleanup `finally` block, flushes the output file, exits.

CLI:
```bash
python run_case.py --case bevy_round10_2bots --flavor bevy --vmid 901
python run_case.py --case arch_round10_4bots --flavor arch --vmid 901 --bots 4
```

Server endpoint (host + port) is baked into the client `.exe` at build time via the Inspector + `BuildModeSwitcher` — the harness doesn't override.

`--runs N` extension (already in the script) loops the above N times back-to-back, producing `<case>_1.jsonl`, `<case>_2.jsonl`, etc. Each run is a separate recorder lifetime and a separate output file.

### 8.3 Workflow for one paper case

```bash
# One server flavour, 10 back-to-back runs at default 270 s + 30 s cooldown:
python run_case.py --case arch_baseline --flavor arch --vmid 901 --runs 10
# Sweep the bot count:
python run_case.py --case arch_2bots --flavor arch --vmid 901 --runs 5 --bots 2
python run_case.py --case arch_4bots --flavor arch --vmid 901 --runs 5 --bots 4
# Repeat per flavour:
python run_case.py --case bevy_baseline --flavor bevy --vmid 901 --runs 10
python run_case.py --case dgs_baseline  --flavor dgs  --vmid 901 --runs 10
```

For paper trials, reboot the server VM between runs (currently a known requirement on the Arch server due to msquic native-state retention — see §10). The Proxmox reboot can be inserted between runs via `qm stop 901 && qm start 901` over SSH.

The wattmeter records to its own log, joined to the .jsonl streams by wall-clock timestamp post-hoc.

---

## 9. Deterministic-workload guarantees (per case)

Across two runs of the same `(flavor, bots, seed)`:

| Layer | Determinism | Mechanism |
|---|---|---|
| Bot input stream | **Bit-identical** | `AutoPilot` is a pure function of `(client_tick, bot_id, seed)` |
| Server RNG output | **Bit-identical** | xoroshiro128+ with same seed |
| Enemy spawn positions | **Bit-identical** | Driven by server RNG only |
| Enemy seek direction | **Bit-identical** | Pure function of integer tick + same float math |
| Enemy integrate / contact resolution | **Bit-identical (Arch); deterministic to solver tolerance (DGS/Bevy/Unreal)** | Arch is contact-free pure float math; the engine legs' solvers may reorder float work (especially multithreaded), so run-to-run identity holds only within each engine's determinism guarantees |
| Snapshot Full content | **Bit-identical iff the resolved world state is** | Pure function of world state + recipient anchor (so it inherits the row above) |
| Snapshot Delta content | **Bit-identical given identical ack stream** | Acks depend on client side, which itself is deterministic given identical inputs and snapshots |
| Wall-clock duration | **Approximately equal** | Within OS scheduling jitter |

Across two **servers** of different flavour with same `(bots, seed)`:

- `Input` wire bytes are bit-identical (the bot is a pure function of `(tick, bot_id, seed)`, independent of the server).
- `Snapshot` wire bytes are **structurally identical but not byte-identical** once real physics is involved: each engine's contact solver reorders per-body float work differently, and Arch resolves no contacts at all, so enemy positions diverge. Enemy *spawn* positions (RNG-driven) and the round/phase timeline match exactly; enemy *resolved* positions do not. The shared contract is the protocol, not the bytes — verified by the absence of `protocol_version`/`sim_tick_ms` mismatch errors and by every snapshot being parseable byte-for-byte.
- This is a deliberate consequence of making the physics engine an independent variable: the workload (same inputs, same spawns, same rules) is fixed; the physics *cost* is what we measure.
- CPU time / memory / network bytes are the dependent variables and naturally differ — that's the experiment.

---

## 10. Known confounds and threats to validity

1. **Physics-model asymmetry (Arch) — the largest current threat.** DGS, Bevy, and Unreal run a real contact solver over ~35k mutually-colliding bodies at round 10; **Arch runs no contacts at all** (kinematic velocity-set + Euler, §3.5). The single most expensive part of the round-10 workload on three legs is simply absent on the fourth. An Arch-vs-{DGS,Bevy,Unreal} CPU/power delta therefore conflates the language/runtime effect with the presence/absence of physics and cannot be read as a clean runtime comparison. Options: (a) treat Arch as an explicit "ECS runtime, no engine physics" data point and compare it only to Unreal's `-UseChaos=false` mode; (b) port Arch to BepuPhysics contacts to close the gap (dependency already referenced). Until (b), report Arch results with this caveat front-and-centre.

2. **Physics-engine fidelity is not identical across the engine legs.** DOTS Physics, avian2d (XPBD), and Chaos use different solvers, contact models, and iteration budgets — Chaos is explicitly tuned down to 2 position / 1 velocity iterations (§2.4) to control CPU, while DOTS and avian run their own defaults. Equal terminal speed and equal collision *semantics* do not imply equal solver *work*. The comparison is "each ecosystem's idiomatic ECS physics at parity gameplay," not "the same solver in three languages." State this framing explicitly in the paper.

3. **Unreal steps physics off the fixed sim tick.** Chaos runs on the UWorld async fixed step and is read back one sim tick later (§6.4), whereas DGS and Bevy step physics inline within the tick. This is a small structural timing difference (one-tick contact lag) that has not been reconciled; note it if strict physics-step parity is claimed.

4. ~~**Outer-loop rate asymmetry**~~ Resolved: all four servers now run at 125 Hz outer / 62.5 Hz sim (§6.4). Per-Update overhead is constant across the comparison.

5. **GC behaviour differs between runtimes**.
   - DGS: Unity uses Boehm-Demers-Weiser (Mono) or BDWGC-flavored (IL2CPP) GC. Behaviour depends on build backend.
   - Arch: .NET Server GC with concurrent collection. Server GC retains committed pages aggressively — between trials, the RSS does not return to baseline without an explicit aggressive Gen2 + LOH compact (currently a known issue worked around by rebooting the VM between trials).
   - Bevy: no GC. Manual memory management via Rust's ownership.
   - Unreal: no tracing GC on the hot path (Mass fragments + Chaos particles are POD/pooled); UObject GC exists but the enemy sim deliberately avoids it (no per-enemy AActors).

6. **msquic native-state retention (Arch only)**. StirlingLabs.MsQuic 23.7.1 appears to retain ~175 MB of native per-connection state per closed connection that is not reclaimed by `Connection.Dispose()` alone. Across back-to-back trials this manifests as a +200 MB RSS growth per trial that does not return to baseline. **Workaround for paper trials**: reboot the server VM between runs via `qm stop 901 && qm start 901`. Alternative fix not yet implemented: replace StirlingLabs.MsQuic with `System.Net.Quic` (built into .NET 7+). (Unreal uses native msquic directly and is not known to exhibit this specific leak, but should be watched with the same reboot-between-runs discipline.)

7. **Microvm base image differs (Debian glibc for DGS + Unreal, Alpine musl for Arch + Bevy)**. Unity and Unreal Linux servers require glibc; Arch and Bevy don't. The kernel is identical (Alpine `linux-virt` apk on all, since `vmlinuz` is fetched independently of the rootfs), but userland libc and supporting libraries differ. Effect on idle power is bounded but should be reported — and it now splits the four legs 2/2 rather than 1/2.

8. **Network jitter affects ack pacing**. Per §9, snapshot Delta composition depends on the per-recipient `last_acked_server_tick`. Different RTT jitter between the QUIC legs (Bevy/Arch/Unreal) and DGS (NGO/UDP) can lead to different Full-vs-Delta ratios over the same trial. The total wire bytes per second is the dependent variable, but composition ratio is a structural difference worth reporting.

9. **AutoPilot fire pattern is open-loop**. The bot fires on a fixed duty cycle (§4.2), not in response to nearby enemies. Total fires per trial are deterministic but unaim-at-targets, so kill counts depend on where enemies happen to walk relative to the rotating beam. This is intentional (removes one confounding variable) but means kill counts are NOT the right metric for "how much hit work did the server do" — total `FireEvent` count is, and that is deterministic.

10. **No friendly fire, no PvP** — by design (§3). Removes a class of behaviours from the workload; trials measure cooperative-survivor workload only.

---

## 11. File-level reference

| Path | Role |
|---|---|
| `DGSvsHS/Assets/_Game/Net/WireFormat.md` | Canonical wire-protocol specification (v4) |
| `DGSvsHS/Assets/_Game/Gameplay/Constants.cs` | Shared constants — DGS/Arch mirror this; Bevy mirrors in `rust/gameplay/src/game/constants.rs`; Unreal in `Unreal/UnrealvsHS/Source/UnrealvsHS/Gameplay/UvHSConstants.h` |
| `DGSvsHS/Assets/_Game/Server/DedicatedServerMain.cs` | DGS Build 1 server entry point |
| `csharp_arch_server/Program.cs` | Arch Build 2 server entry point |
| `csharp_arch_server/Net/QuicServer.cs` | Arch QUIC transport (msquic) |
| `rust/cli/src/main.rs` | Bevy Build 3 server entry point |
| `rust/gameplay/src/network/plugin.rs` | Bevy QUIC transport (quiche) |
| `rust/gameplay/src/server/broadcast.rs` | Bevy snapshot composition / broadcast |
| `rust/gameplay/src/game/sim/movement.rs` / `round.rs` | Bevy enemy seek/drive + avian2d body spawn |
| `Unreal/UnrealvsHS/Source/UnrealvsHS/Server/UvHSServerGameMode.cpp` | Unreal Build 4 server entry point / lifecycle |
| `Unreal/UnrealvsHS/Source/UnrealvsHS/Server/UvHSEnemyBodyStore.cpp` | Unreal Chaos rigid-body particle store (physics core) |
| `Unreal/UnrealvsHS/Source/UnrealvsHS/Server/SimSystems.cpp` / `SimRunner.cpp` | Unreal sim sub-steps + fixed-step runner |
| `Unreal/UnrealvsHS/Source/UnrealvsHS/Net/QuicServer.cpp` | Unreal QUIC transport (native msquic) |
| `Unreal/UnrealvsHS/Config/DefaultEngine.ini` | Unreal physics settings + Chaos solver iteration budget (`[ConsoleVariables]`) |
| `DGSvsHS/Assets/_Game/Server/Dots/Systems/` | DGS DOTS physics + sim systems (`EnemySeekSystem`, `PhysicsSyncSystems`, …) |
| `DGSvsHS/Assets/_Game/Client/ClientMain.cs` | Shared Unity client orchestrator |
| `DGSvsHS/Assets/_Game/Client/AutoPilot.cs` | Deterministic bot driver |
| `DGSvsHS/Assets/_Game/Net/Quic/QuicNetworkClient.cs` | Client HS-mode transport (P/Invoke to native quinn) |
| `native/quic_client/` | Native QUIC client (Rust dylib loaded by Unity HS) |
| `test_harness/run_case.py` | Per-case orchestrator |
| `test_harness/record_run.py` | Proxmox-host SSH telemetry recorder |
| `CLAUDE.md` | Project-internal handoff/context document for AI assistants |
| `README.md` | High-level project README |
| **`METHODOLOGY.md`** (this file) | Paper-companion methodology reference |
