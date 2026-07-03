/// Enter a Tracy/tracing span for the enclosing scope. Put it at the top of a
/// hot-path method: `crate::hot_span!("select_for_delta");`. The guard is bound
/// to `_hot_span`, so it closes when the scope ends.
///
/// Gated behind the `tracy` cargo feature: with it off (the default), this
/// expands to nothing — zero overhead for power-trial builds. Enable it with
/// `--features tracy` to stream to a Tracy profiler.
///
/// Only span per-tick / per-fire / per-recipient methods. Do NOT span per-enemy
/// inner loops (grid.insert, segment_circle_hit, …): millions of span
/// enter/exits per tick would swamp Tracy and distort the timings you're trying
/// to read.
#[cfg(feature = "tracy")]
#[macro_export]
macro_rules! hot_span {
    ($name:expr) => {
        let _hot_span = bevy::log::info_span!($name).entered();
    };
}

#[cfg(not(feature = "tracy"))]
#[macro_export]
macro_rules! hot_span {
    ($name:expr) => {};
}

/// Expression form of [`hot_span!`] for phases that must end before the scope
/// does: bind the returned guard and `drop(guard)` at the phase boundary. With
/// the `tracy` feature off it yields `()`, so `drop(guard)` is a harmless no-op.
#[cfg(feature = "tracy")]
#[macro_export]
macro_rules! hot_span_guard {
    ($name:expr) => {
        bevy::log::info_span!($name).entered()
    };
}

#[cfg(not(feature = "tracy"))]
#[macro_export]
macro_rules! hot_span_guard {
    ($name:expr) => {
        $crate::NoopSpan
    };
}

/// Stand-in guard returned by [`hot_span_guard!`] when the `tracy` feature is
/// off. Zero-sized and deliberately non-`Copy` so `drop(guard)` at a phase
/// boundary is a real (no-op) move, not a lint-triggering drop-of-Copy.
#[doc(hidden)]
pub struct NoopSpan;

mod system_metrics;
pub mod game;
pub mod network;
pub mod server;

use std::time::Duration;
use avian2d::prelude::*;
use bevy::app::{AppExit, ScheduleRunnerPlugin};
use bevy::diagnostic::{FrameTimeDiagnosticsPlugin, LogDiagnosticsPlugin};
use bevy::log::{LogPlugin, info};
use bevy::prelude::*;

use mimalloc::MiMalloc;

#[global_allocator]
static GLOBAL: MiMalloc = MiMalloc;

/// CLI-driven server config. Mirrors the relevant subset of
/// `csharp_arch_server/Program.cs::Config`.
#[derive(Clone, Debug)]
pub struct ServerConfig {
    pub seed: u64,
    pub god_mode: bool,
    /// If set, the server exits after this many wall-clock seconds (trial mode).
    pub run_for_seconds: Option<f32>,
}

impl Default for ServerConfig {
    fn default() -> Self {
        Self {
            seed: 0xC0FF_EEF0_0D,
            god_mode: false,
            run_for_seconds: None,
        }
    }
}

fn setup_server(cfg: Res<ServerConfigResource>) {
    info!(
        "DGSvsHS Bevy server boot — seed=0x{:X} godMode={} (sim + QUIC + per-recipient delta snapshots)",
        cfg.0.seed, cfg.0.god_mode
    );
}

#[derive(Resource, Clone)]
struct ServerConfigResource(pub ServerConfig);

#[derive(Resource)]
struct DurationCap {
    started: std::time::Instant,
    deadline: Duration,
}

pub fn launch_server(cfg: ServerConfig) {
    // Outer schedule rate = 125 Hz (8 ms), sim rate = 62.5 Hz (16 ms via
    // Time::<Fixed>::from_hz in SimPlugin). The 2:1 integer ratio means
    // FixedUpdate fires *deterministically* every other outer Update — no
    // accumulator jitter (jitter would only appear with a non-integer ratio
    // like the old 240:62.5 = 3.84:1). All three servers (DGS/Arch/Bevy)
    // run this exact 125/62.5 cadence so per-Update overhead is identical
    // across the comparison.
    let tick_period = Duration::from_secs_f64(1.0 / 125.0);

    let mut app = App::new();
    app.add_plugins(MinimalPlugins.set(ScheduleRunnerPlugin::run_loop(tick_period)))
        .add_plugins(LogPlugin::default())
        .add_plugins(FrameTimeDiagnosticsPlugin::default())
        .add_plugins(LogDiagnosticsPlugin {
            wait_duration: Duration::from_secs(5),
            ..Default::default()
        })
        .add_plugins(system_metrics::MicroSystemMetrics)
        .add_plugins(PhysicsPlugins::new(FixedUpdate))
        .insert_resource(Gravity(Vec2::ZERO))
        // Cut Avian's per-step substep solve from the default 6 (see
        // constants::PHYSICS_SUBSTEPS). Overrides Avian's init_resource default.
        .insert_resource(SubstepCount(game::constants::PHYSICS_SUBSTEPS))
        .add_plugins(game::sim::SimPlugin {
            seed: cfg.seed,
            god_mode: cfg.god_mode,
        })
        .add_plugins(network::NetworkPlugin)
        .add_plugins(server::ServerPlugin)
        .insert_resource(ServerConfigResource(cfg.clone()))
        .add_systems(Startup, setup_server);

    // Spiral-of-death guard. `Time<Fixed>` catches up to wall-clock by running
    // FixedUpdate repeatedly ("overstep") to drain the accumulator, and
    // `Time<Virtual>::max_delta` caps how much real time can be fed into that
    // accumulator per outer Update. Bevy's default cap is 250 ms — at our 16 ms
    // tick that's up to ~15 FixedUpdate runs bunched into one frame. Once a
    // single tick's work exceeds 16 ms of wall time (CPU at 100 %), every frame
    // tries to run ~15 ticks, each now even slower → runaway that kills the
    // server. For a power benchmark we want the opposite: if the CPU can't keep
    // up, DROP ticks (let sim time fall behind wall-clock) rather than thrash.
    // Clamping max_delta to one tick means at most 1–2 catch-up steps per frame,
    // so overload degrades into graceful slowdown instead of a spiral.
    app.world_mut()
        .resource_mut::<Time<Virtual>>()
        .set_max_delta(Duration::from_secs_f64(game::constants::SIM_DT as f64));

    if let Some(secs) = cfg.run_for_seconds {
        app.insert_resource(DurationCap {
            started: std::time::Instant::now(),
            deadline: Duration::from_secs_f32(secs),
        });
        app.add_systems(Last, duration_cap_system);
    }

    app.run();
}

fn duration_cap_system(cap: Res<DurationCap>, mut exit: MessageWriter<AppExit>) {
    if cap.started.elapsed() >= cap.deadline {
        info!(
            "[server] duration cap reached ({}s) — shutting down",
            cap.deadline.as_secs_f32()
        );
        exit.write(AppExit::Success);
    }
}
