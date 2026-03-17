use std::os::windows::process::CommandExt;

/// Windows flag to suppress console window when spawning cmd.exe.
const CREATE_NO_WINDOW: u32 = 0x08000000;

/// Gracefully restart the application.
///
/// Spawns a delayed re-launch (2s via `cmd /c timeout`) then exits the current
/// process.  The delay ensures the named mutex is released before the new
/// instance tries to acquire it.
pub fn restart_app() -> ! {
    tracing::info!("App restart initiated — spawning delayed re-launch");

    // Flush pending config saves
    crate::config::flush_pending_saves();

    // Stop heartbeat synchronously
    crate::backend::heartbeat::stop_heartbeat_sync(Some("app_restart".into()));

    // Launch a delayed restart
    if let Ok(exe) = std::env::current_exe() {
        let cmd = format!(
            "timeout /t 2 /nobreak >nul && \"{}\"",
            exe.display()
        );
        let _ = std::process::Command::new("cmd")
            .args(["/c", &cmd])
            .creation_flags(CREATE_NO_WINDOW)
            .spawn();
    }

    crate::platform::tray::close_app();
    // close_app() posts WM_CLOSE asynchronously; give it time to close gracefully,
    // then fall back to hard exit if it didn't (shouldn't happen).
    std::thread::sleep(std::time::Duration::from_secs(3));
    std::process::exit(0)
}
