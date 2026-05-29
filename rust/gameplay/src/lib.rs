mod system_metrics;
pub mod game;
pub mod network;

use std::time::Duration;
use bevy::app::ScheduleRunnerPlugin;
use bevy::diagnostic::{FrameTimeDiagnosticsPlugin, LogDiagnosticsPlugin};
use bevy::log::{LogPlugin, info};
use bevy::prelude::*;

use mimalloc::MiMalloc;

#[global_allocator]
static GLOBAL: MiMalloc = MiMalloc;

pub fn setup_server() {
    info!("Welcome to the playground! ECS is alive.");
}

pub fn launch_server(){
    App::new()
        .add_plugins(MinimalPlugins.set(ScheduleRunnerPlugin::run_loop(
            Duration::from_secs_f64(1.0 / 240.0),
        )))
        .add_plugins(LogPlugin::default())
        .add_plugins(FrameTimeDiagnosticsPlugin::default())
        .add_plugins(LogDiagnosticsPlugin {
            wait_duration: Duration::from_secs(5),
            ..Default::default()
        })
        .add_plugins(system_metrics::MicroSystemMetrics)
        .add_plugins(network::NetworkPlugin)
        .add_plugins(game::StressPlugin)
        .add_systems(Startup, setup_server)
        .run();
}