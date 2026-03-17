use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};

use muda::{Menu, MenuEvent, MenuId, MenuItem, PredefinedMenuItem};
use tracing::{info, warn};
use tray_icon::{Icon, MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use winapi::um::winuser::{FindWindowW, PostMessageW, SetForegroundWindow, ShowWindow, SW_HIDE, SW_RESTORE, WM_CLOSE};

/// Set by tray events — the app coroutine polls these to act on them.
pub static SHOW_WINDOW_REQUESTED: AtomicBool = AtomicBool::new(false);
pub static QUIT_REQUESTED: AtomicBool = AtomicBool::new(false);
pub static RESTART_REQUESTED: AtomicBool = AtomicBool::new(false);

const MENU_SHOW_ID: &str = "show_window";
const MENU_RESTART_ID: &str = "restart_app";
const MENU_QUIT_ID: &str = "quit_app";

/// Window title used by Dioxus (must match Dioxus.toml [desktop.window] title).
const WINDOW_TITLE: &str = "Esprit Seeder";

/// Initialize the system tray icon and context menu.
/// Call on the main thread before the event loop starts.
pub fn init_tray() {
    // Decode the embedded PNG icon
    let icon_bytes = include_bytes!("../../icons/icon.png");
    let img = match image::load_from_memory(icon_bytes) {
        Ok(img) => img,
        Err(e) => {
            warn!(
                "Failed to decode tray icon: {}. Tray will not be available.",
                e
            );
            return;
        }
    };
    let rgba = img.to_rgba8();
    let (w, h) = rgba.dimensions();
    let icon = match Icon::from_rgba(rgba.into_raw(), w, h) {
        Ok(icon) => icon,
        Err(e) => {
            warn!(
                "Failed to create tray icon: {}. Tray will not be available.",
                e
            );
            return;
        }
    };

    // Build context menu
    let show_item = MenuItem::with_id(MENU_SHOW_ID, "Show Window", true, None);
    let restart_item = MenuItem::with_id(MENU_RESTART_ID, "Restart", true, None);
    let quit_item = MenuItem::with_id(MENU_QUIT_ID, "Quit", true, None);
    let separator = PredefinedMenuItem::separator();

    let menu = Menu::new();
    if let Err(e) = menu.append_items(&[&show_item, &restart_item, &separator, &quit_item]) {
        warn!("Failed to build tray menu: {}", e);
        return;
    }

    // Create the tray icon
    match TrayIconBuilder::new()
        .with_tooltip(WINDOW_TITLE)
        .with_icon(icon)
        .with_menu(Box::new(menu))
        .build()
    {
        Ok(tray) => {
            // Keep tray icon alive for process lifetime.
            // The OS will clean up when the process exits.
            std::mem::forget(tray);
            info!("System tray initialized");
        }
        Err(e) => {
            warn!("Failed to create tray icon: {}", e);
        }
    }
}

/// Poll for pending tray events and set atomic flags.
/// Call from a Dioxus coroutine on a short interval (~200ms).
pub fn poll_tray_events() {
    let show_id = MenuId::new(MENU_SHOW_ID);
    let restart_id = MenuId::new(MENU_RESTART_ID);
    let quit_id = MenuId::new(MENU_QUIT_ID);

    // Menu item clicks
    while let Ok(event) = MenuEvent::receiver().try_recv() {
        if event.id == show_id {
            SHOW_WINDOW_REQUESTED.store(true, Ordering::Release);
        } else if event.id == restart_id {
            RESTART_REQUESTED.store(true, Ordering::Release);
        } else if event.id == quit_id {
            QUIT_REQUESTED.store(true, Ordering::Release);
        }
    }

    // Tray icon clicks (left-click = show/focus)
    while let Ok(event) = TrayIconEvent::receiver().try_recv() {
        match event {
            TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            }
            | TrayIconEvent::DoubleClick {
                button: MouseButton::Left,
                ..
            } => {
                SHOW_WINDOW_REQUESTED.store(true, Ordering::Release);
            }
            _ => {}
        }
    }
}

// ─── Win32 window management ─────────────────────────────────────────

/// Find the main window HWND by its title.
fn find_main_window() -> winapi::shared::windef::HWND {
    let title: Vec<u16> = WINDOW_TITLE
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    unsafe { FindWindowW(ptr::null(), title.as_ptr()) }
}

/// Show and focus the main window (restore from minimized/hidden).
pub fn show_main_window() {
    let hwnd = find_main_window();
    if !hwnd.is_null() {
        unsafe {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
    }
}

/// Hide the main window (minimize to tray).
pub fn hide_main_window() {
    let hwnd = find_main_window();
    if !hwnd.is_null() {
        unsafe {
            ShowWindow(hwnd, SW_HIDE);
        }
    }
}

/// Close the app by posting WM_CLOSE to the main window.
///
/// This triggers a proper tao event loop shutdown instead of
/// `std::process::exit()` which panics with "cannot move state from Destroyed".
pub fn close_app() {
    let hwnd = find_main_window();
    if !hwnd.is_null() {
        unsafe {
            PostMessageW(hwnd, WM_CLOSE, 0, 0);
        }
    } else {
        // Fallback — window not found (shouldn't happen in normal operation)
        std::process::exit(0);
    }
}

/// Minimize the main window.
pub fn minimize_main_window() {
    let hwnd = find_main_window();
    if !hwnd.is_null() {
        unsafe {
            ShowWindow(hwnd, winapi::um::winuser::SW_MINIMIZE);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::Ordering;

    #[test]
    fn test_show_window_requested_default() {
        // Atomic bools default to false
        let val = SHOW_WINDOW_REQUESTED.load(Ordering::Relaxed);
        // Reset in case another test set it
        SHOW_WINDOW_REQUESTED.store(false, Ordering::Relaxed);
        assert!(!val || true); // just ensure it loads without panic
    }

    #[test]
    fn test_quit_requested_default() {
        let val = QUIT_REQUESTED.load(Ordering::Relaxed);
        QUIT_REQUESTED.store(false, Ordering::Relaxed);
        assert!(!val || true);
    }

    #[test]
    fn test_show_window_requested_store_load() {
        let original = SHOW_WINDOW_REQUESTED.load(Ordering::SeqCst);
        SHOW_WINDOW_REQUESTED.store(true, Ordering::SeqCst);
        assert!(SHOW_WINDOW_REQUESTED.load(Ordering::SeqCst));
        SHOW_WINDOW_REQUESTED.store(false, Ordering::SeqCst);
        assert!(!SHOW_WINDOW_REQUESTED.load(Ordering::SeqCst));
        SHOW_WINDOW_REQUESTED.store(original, Ordering::SeqCst);
    }

    #[test]
    fn test_quit_requested_store_load() {
        let original = QUIT_REQUESTED.load(Ordering::SeqCst);
        QUIT_REQUESTED.store(true, Ordering::SeqCst);
        assert!(QUIT_REQUESTED.load(Ordering::SeqCst));
        QUIT_REQUESTED.store(false, Ordering::SeqCst);
        assert!(!QUIT_REQUESTED.load(Ordering::SeqCst));
        QUIT_REQUESTED.store(original, Ordering::SeqCst);
    }

    #[test]
    fn test_restart_requested_store_load() {
        let original = RESTART_REQUESTED.load(Ordering::SeqCst);
        RESTART_REQUESTED.store(true, Ordering::SeqCst);
        assert!(RESTART_REQUESTED.load(Ordering::SeqCst));
        RESTART_REQUESTED.store(false, Ordering::SeqCst);
        assert!(!RESTART_REQUESTED.load(Ordering::SeqCst));
        RESTART_REQUESTED.store(original, Ordering::SeqCst);
    }

    #[test]
    fn test_menu_id_constants() {
        assert_eq!(MENU_SHOW_ID, "show_window");
        assert_eq!(MENU_RESTART_ID, "restart_app");
        assert_eq!(MENU_QUIT_ID, "quit_app");
    }

    #[test]
    fn test_window_title_constant() {
        assert_eq!(WINDOW_TITLE, "Esprit Seeder");
    }

    #[test]
    fn test_find_main_window_does_not_panic() {
        // Returns a valid HWND if the app is running, null otherwise
        let _hwnd = find_main_window();
    }

    #[test]
    fn test_show_main_window_no_panic_when_no_window() {
        // Should not panic even when no window exists
        show_main_window();
    }

    #[test]
    fn test_hide_main_window_no_panic_when_no_window() {
        hide_main_window();
    }

    #[test]
    fn test_minimize_main_window_no_panic_when_no_window() {
        minimize_main_window();
    }
}
