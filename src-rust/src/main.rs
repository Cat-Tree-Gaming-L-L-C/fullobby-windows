#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

// macros must be declared first so other modules can use lock!/read_lock!/write_lock!
#[macro_use]
mod macros;

mod api;
mod app;
mod backend;
mod components;
mod config;
mod error;
mod events;
mod platform;
mod state;

use dioxus::prelude::*;
use dioxus_desktop::{Config, WindowBuilder};
use tracing::info;
use tracing_subscriber::EnvFilter;

fn main() {
    // Initialize logging
    let log_dir = dirs::data_local_dir()
        .unwrap_or_else(std::env::temp_dir)
        .join("org.espritdecorpsgaming.espritseeder")
        .join("logs");
    if let Err(e) = std::fs::create_dir_all(&log_dir) {
        eprintln!("Warning: could not create log directory {:?}: {}", log_dir, e);
    }

    let file_appender = tracing_appender::rolling::RollingFileAppender::builder()
        .rotation(tracing_appender::rolling::Rotation::DAILY)
        .filename_prefix("esprit-seeder")
        .filename_suffix("log")
        .max_log_files(7)
        .build(&log_dir);

    // _guard must live until the end of main to ensure log flushing
    let _guard: Box<dyn std::any::Any>;

    match file_appender {
        Ok(appender) => {
            let (non_blocking, guard) = tracing_appender::non_blocking(appender);
            _guard = Box::new(guard);
            tracing_subscriber::fmt()
                .with_env_filter(
                    EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info")),
                )
                .with_writer(non_blocking)
                .with_ansi(false)
                .init();
        }
        Err(e) => {
            eprintln!("Warning: could not create log file appender: {} — logging to stderr only", e);
            _guard = Box::new(());
            tracing_subscriber::fmt()
                .with_env_filter(
                    EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info")),
                )
                .with_writer(std::io::stderr)
                .with_ansi(false)
                .init();
        }
    }

    // Initialize tracing-log bridge so `log::info!()` in backend modules works
    // (tracing-subscriber already captures log macros when tracing-log feature is enabled)

    // Install panic hook that writes to the log file before aborting.
    // With panic=abort in release mode, panics kill the process instantly.
    // Without this hook, the panic message goes to stderr (invisible with
    // windows_subsystem="windows") and the non-blocking log buffer is never flushed.
    let panic_log_dir = log_dir.clone();
    std::panic::set_hook(Box::new(move |info| {
        let msg = format!("PANIC: {}", info);
        // Write directly to a crash log file (bypasses the non-blocking buffer)
        let crash_path = panic_log_dir.join("crash.log");
        let timestamp = chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f");
        let entry = format!("[{}] {}\n", timestamp, msg);
        let _ = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&crash_path)
            .and_then(|mut f| std::io::Write::write_all(&mut f, entry.as_bytes()));
        // Also try tracing (may not flush before abort, but worth trying)
        tracing::error!("{}", msg);
    }));

    info!("Esprit Seeder v{} starting", env!("CARGO_PKG_VERSION"));

    // Single instance check
    if !platform::single_instance::acquire_single_instance("Global\\EspritSeeder") {
        info!("Another instance is already running, forwarding args");
        let args: Vec<String> = std::env::args().collect();
        platform::single_instance::send_args_to_running_instance(&args);
        std::process::exit(0);
    }

    // Initialize event bus
    events::init_event_bus();

    // Start listening for args from future instances (deep links, --autoseed-na, etc.)
    platform::single_instance::start_single_instance_listener();

    // Update "Start with Windows" registry path if it changed (e.g. after NSIS update)
    platform::startup::update_startup_path_if_needed();

    // Run setup (config init, CLI arg handling, etc.)
    // This is done synchronously before Dioxus launch so config is ready
    backend::setup::run_setup();

    // Register deep link protocol
    platform::deep_link::register_deep_link_protocol();

    // Initialize system tray (must be on main thread before event loop)
    platform::tray::init_tray();

    // Launch Dioxus desktop app with frameless window (custom titlebar)
    LaunchBuilder::new()
        .with_cfg(desktop! {
            Config::new()
                .with_window(
                    WindowBuilder::new()
                        .with_title("Esprit Seeder")
                        .with_inner_size(dioxus_desktop::LogicalSize::new(500.0, 520.0))
                        .with_resizable(false)
                        .with_maximizable(false)
                        .with_decorations(false)
                )
                .with_disable_context_menu(true)
        })
        .launch(app::App);
}
