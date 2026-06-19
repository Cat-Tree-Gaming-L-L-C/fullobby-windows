extern crate winapi;

use std::ffi::OsString;
use std::os::windows::ffi::OsStringExt;
use std::ptr::null_mut;
use std::sync::RwLock;
use std::time::Instant;
use once_cell::sync::Lazy;
use winapi::shared::minwindef::{BOOL, LPARAM, WPARAM};
use winapi::shared::windef::HWND;
use winapi::um::processthreadsapi::GetCurrentThreadId;
use winapi::um::winuser::{
    AttachThreadInput, EnumWindows, GetForegroundWindow, GetWindowTextLengthW, GetWindowTextW,
    GetWindowThreadProcessId, IsWindow, PostMessageW, SetForegroundWindow, SetWindowPos, ShowWindow,
    SWP_NOMOVE, SWP_NOSIZE, SWP_SHOWWINDOW, SW_MINIMIZE, SW_RESTORE, VK_ESCAPE, WM_KEYDOWN, WM_KEYUP,
};

// F13 (0x7C) — a real virtual key that UE4 registers as "any button pressed"
// but no game ever binds to anything. Safe to spam without side effects.
// NOTE: F13 does NOT skip intro videos in HLL - only works for the splash screen.
const VK_F13: i32 = 0x7C;

/// Wrapper for HWND that is Send+Sync safe.
/// HWND is just a pointer/handle - the Windows API is thread-safe for window handles.
#[derive(Clone, Copy)]
struct SendHwnd(HWND);

// SAFETY: HWND is just an opaque handle that Windows API treats as thread-safe.
// The Windows API documentation states that window handles can be used across threads.
unsafe impl Send for SendHwnd {}
unsafe impl Sync for SendHwnd {}

/// Cached HWND with timestamp for TTL checking
struct CachedHwnd {
    hwnd: SendHwnd,
    timestamp: Instant,
}

// HWND cache - short TTL since windows can close/recreate quickly
static HWND_CACHE: Lazy<RwLock<Option<CachedHwnd>>> = Lazy::new(|| RwLock::new(None));
const HWND_CACHE_TTL_MS: u128 = 1000;  // 1 second

/// Convert a Windows wide string to a Rust string.
/// Returns None if the pointer is null or length is invalid.
fn from_wide_ptr(ptr: *const u16, len: i32) -> Option<String> {
    if ptr.is_null() || len < 0 {
        return None;
    }
    let slice = unsafe { std::slice::from_raw_parts(ptr, len as usize) };
    Some(OsString::from_wide(slice).to_string_lossy().into_owned())
}

/// Validate that an HWND is still a valid window handle.
fn is_valid_hwnd(hwnd: HWND) -> bool {
    if hwnd.is_null() {
        return false;
    }
    unsafe { IsWindow(hwnd) != 0 }
}

/// Validate that an HWND is still a valid window AND still belongs to HLL.
/// Prevents stale-handle misdirection if the original window was destroyed
/// and Windows recycled the HWND for a different process.
fn is_valid_hll_hwnd(hwnd: HWND) -> bool {
    if !is_valid_hwnd(hwnd) {
        return false;
    }
    unsafe {
        let len = GetWindowTextLengthW(hwnd);
        if len <= 0 {
            return false;
        }
        let buf_len = (len + 1) as usize;
        let mut buffer: Vec<u16> = vec![0u16; buf_len];
        GetWindowTextW(hwnd, buffer.as_mut_ptr(), buf_len as i32);
        match from_wide_ptr(buffer.as_ptr(), len) {
            Some(title) => title.contains("Hell Let Loose"),
            None => false,
        }
    }
}

unsafe extern "system" fn focus_esprit_seeder_enumerate(hwnd: HWND, _l_param: LPARAM) -> BOOL {
    handle_window(hwnd, "Esprit Seeder")
}

// Callback that writes the found HWND into the pointer passed via l_param
unsafe extern "system" fn find_hll_enumerate(hwnd: HWND, l_param: LPARAM) -> BOOL {
    let len = GetWindowTextLengthW(hwnd) + 1;
    if len > 1 {
        let mut buffer: Vec<u16> = vec![0u16; len as usize];
        GetWindowTextW(hwnd, buffer.as_mut_ptr(), len);
        if let Some(window_name) = from_wide_ptr(buffer.as_ptr(), len - 1) {
            if window_name.contains("Hell Let Loose") {
                let result_ptr = l_param as *mut HWND;
                *result_ptr = hwnd;
                return 0; // Stop enumeration
            }
        }
    }
    1 // Continue enumeration
}

/// Find the HLL window handle by enumerating all top-level windows.
/// Uses caching to avoid repeated enumeration within short intervals.
/// Validates that the returned HWND is still a valid window.
pub fn find_hll_hwnd() -> Option<HWND> {
    let now = Instant::now();

    // Check cache first (read lock) — verify title to prevent stale-handle misdirection
    {
        let cache = read_lock!(HWND_CACHE);
        if let Some(ref cached) = *cache {
            if now.duration_since(cached.timestamp).as_millis() < HWND_CACHE_TTL_MS {
                if is_valid_hll_hwnd(cached.hwnd.0) {
                    return Some(cached.hwnd.0);
                }
            }
        }
    }

    // Cache miss or invalid - enumerate windows
    let mut result: HWND = null_mut();
    unsafe {
        EnumWindows(
            Some(find_hll_enumerate),
            &mut result as *mut HWND as LPARAM,
        );
    }

    if result.is_null() {
        // Clear cache when window not found
        let mut cache = write_lock!(HWND_CACHE);
        *cache = None;
        return None;
    }

    if !is_valid_hwnd(result) {
        let mut cache = write_lock!(HWND_CACHE);
        *cache = None;
        return None;
    }

    // Update cache (write lock)
    {
        let mut cache = write_lock!(HWND_CACHE);
        *cache = Some(CachedHwnd {
            hwnd: SendHwnd(result),
            timestamp: now,
        });
    }

    Some(result)
}

/// Invalidate the HWND cache. Call when game is killed/closed.
pub fn invalidate_hwnd_cache() {
    let mut cache = write_lock!(HWND_CACHE);
    *cache = None;
}

/// Send keys to the HLL window via PostMessage only.
/// This is completely silent — no focus stealing, no mouse, no effect on the
/// user's desktop.
///
/// When `send_escape` is true (video-skipping phase), sends Escape to skip
/// intro videos. Always sends F13 as the "any button" press — F13 is a valid
/// virtual key that UE4 sees as input but is never bound to anything in-game,
/// so it won't open menus or start squads.
///
/// Returns Ok(true) if the window was found, Ok(false) if not.
///
/// Note: Uses std::thread::sleep (blocking) instead of tokio::time::sleep because:
/// 1. These are 50ms delays for Windows API timing between PostMessage calls
/// 2. The delays ensure reliable key press/release registration
/// 3. This function is intentionally synchronous for Windows API interaction
/// 4. The brief blocking time is negligible and doesn't warrant async overhead
pub fn send_keys_to_hll(send_escape: bool) -> Result<bool, String> {
    let hwnd = match find_hll_hwnd() {
        Some(h) => h,
        None => return Ok(false),
    };

    unsafe {
        if send_escape {
            // Re-validate HWND + title before sending Escape
            if !is_valid_hll_hwnd(hwnd) {
                return Ok(false);
            }
            // Escape - skips UE4 intro videos (only during video phase)
            PostMessageW(hwnd, WM_KEYDOWN, VK_ESCAPE as WPARAM, 0);
            std::thread::sleep(std::time::Duration::from_millis(50));
            PostMessageW(hwnd, WM_KEYUP, VK_ESCAPE as WPARAM, 0);
        }

        // Re-validate HWND + title before sending F13
        if !is_valid_hll_hwnd(hwnd) {
            return Ok(false);
        }
        // F13 - dismisses "press any button to continue" splash screen.
        // Harmless in-game (nothing is bound to F13).
        PostMessageW(hwnd, WM_KEYDOWN, VK_F13 as WPARAM, 0);
        std::thread::sleep(std::time::Duration::from_millis(50));
        PostMessageW(hwnd, WM_KEYUP, VK_F13 as WPARAM, 0);
    }

    Ok(true)
}

/// Force-focus the HLL window using the AttachThreadInput trick (bypasses
/// Windows 11 foreground lock restrictions), then send F13 via PostMessage.
/// This WILL briefly steal focus from the user's active window.
/// Returns Ok(true) if the window was found, Ok(false) if not.
///
/// Note: Uses std::thread::sleep (blocking) instead of tokio::time::sleep because:
/// 1. This is a 50ms delay for Windows API timing between PostMessage calls
/// 2. The delay ensures reliable key press/release registration
/// 3. This function is intentionally synchronous for Windows API interaction
/// 4. The brief blocking time is negligible and doesn't warrant async overhead
pub fn force_focus_and_send_key() -> Result<bool, String> {
    let hwnd = match find_hll_hwnd() {
        Some(h) => h,
        None => return Ok(false),
    };

    unsafe {
        // Validate HWND + title before use
        if !is_valid_hll_hwnd(hwnd) {
            return Ok(false);
        }

        ShowWindow(hwnd, SW_RESTORE);

        // AttachThreadInput trick: attach our input queue to the foreground
        // window's thread so Windows allows SetForegroundWindow.
        let foreground_hwnd = GetForegroundWindow();
        let foreground_thread = GetWindowThreadProcessId(foreground_hwnd, null_mut());
        let our_thread = GetCurrentThreadId();

        let attached = if foreground_thread != our_thread && foreground_thread != 0 {
            AttachThreadInput(our_thread, foreground_thread, 1) != 0
        } else {
            false
        };

        // Re-validate + title check before SetForegroundWindow (window may have closed)
        if is_valid_hll_hwnd(hwnd) {
            SetForegroundWindow(hwnd);
            SetWindowPos(
                hwnd,
                null_mut(),
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW,
            );
        }

        if attached {
            AttachThreadInput(our_thread, foreground_thread, 0);
        }

        // Re-validate + title check before PostMessage
        if !is_valid_hll_hwnd(hwnd) {
            return Ok(false);
        }

        // F13 via PostMessage — harmless in-game
        PostMessageW(hwnd, WM_KEYDOWN, VK_F13 as WPARAM, 0);
        std::thread::sleep(std::time::Duration::from_millis(50));
        PostMessageW(hwnd, WM_KEYUP, VK_F13 as WPARAM, 0);
    }

    Ok(true)
}

// Helper function to handle window focusing (used by focus_esprit_seeder)
unsafe fn handle_window(hwnd: HWND, window_title: &str) -> BOOL {
    let len = GetWindowTextLengthW(hwnd) + 1;
    if len > 1 {
        let mut buffer: Vec<u16> = vec![0u16; len as usize];

        GetWindowTextW(hwnd, buffer.as_mut_ptr(), len);

        if let Some(window_name) = from_wide_ptr(buffer.as_ptr(), len - 1) {
            if window_name.contains(window_title) {
                // Validate HWND before using it
                if !is_valid_hwnd(hwnd) {
                    return 1; // Continue enumeration - this window is no longer valid
                }

                ShowWindow(hwnd, SW_RESTORE);
                if SetForegroundWindow(hwnd) == 0 {
                    log::warn!("Failed to set foreground window: {:?}", std::io::Error::last_os_error());
                }

                if SetWindowPos(hwnd, null_mut(), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW) == 0 {
                    log::warn!("Failed to set window position: {:?}", std::io::Error::last_os_error());
                }
                return 0; // Stop enumeration
            }
        }
    }
    1 // Continue enumeration
}

pub fn focus_esprit_seeder() -> Result<(), String> {
    unsafe {
        EnumWindows(Some(focus_esprit_seeder_enumerate), 0);
    }
    Ok(())
}

/// Minimize the HLL window for efficiency mode.
/// Returns Ok(true) if the window was found and minimized, Ok(false) if not found.
pub fn minimize_hll_window() -> Result<bool, String> {
    let hwnd = match find_hll_hwnd() {
        Some(h) => h,
        None => return Ok(false),
    };

    unsafe {
        if !is_valid_hll_hwnd(hwnd) {
            return Ok(false);
        }
        ShowWindow(hwnd, SW_MINIMIZE);
    }

    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_from_wide_ptr_null() {
        assert_eq!(from_wide_ptr(std::ptr::null(), 0), None);
    }

    #[test]
    fn test_from_wide_ptr_negative_len() {
        let data: Vec<u16> = vec![72, 105]; // "Hi"
        assert_eq!(from_wide_ptr(data.as_ptr(), -1), None);
    }

    #[test]
    fn test_from_wide_ptr_empty() {
        let data: Vec<u16> = vec![];
        let result = from_wide_ptr(data.as_ptr(), 0);
        assert_eq!(result, Some(String::new()));
    }

    #[test]
    fn test_from_wide_ptr_ascii() {
        // "Hello" in UTF-16
        let data: Vec<u16> = vec![72, 101, 108, 108, 111];
        let result = from_wide_ptr(data.as_ptr(), 5);
        assert_eq!(result, Some("Hello".to_string()));
    }

    #[test]
    fn test_from_wide_ptr_unicode() {
        // "Hell Let Loose" in UTF-16
        let text = "Hell Let Loose";
        let data: Vec<u16> = text.encode_utf16().collect();
        let result = from_wide_ptr(data.as_ptr(), data.len() as i32);
        assert_eq!(result, Some("Hell Let Loose".to_string()));
    }

    #[test]
    fn test_from_wide_ptr_with_special_chars() {
        let text = "Esprit — Seeder";
        let data: Vec<u16> = text.encode_utf16().collect();
        let result = from_wide_ptr(data.as_ptr(), data.len() as i32);
        assert_eq!(result, Some("Esprit — Seeder".to_string()));
    }

    #[test]
    fn test_is_valid_hwnd_null() {
        assert!(!is_valid_hwnd(std::ptr::null_mut()));
    }

    #[test]
    fn test_invalidate_hwnd_cache() {
        // Just ensure it doesn't panic
        invalidate_hwnd_cache();
        // After invalidation, cache should be None
        let cache = read_lock!(HWND_CACHE);
        assert!(cache.is_none());
    }

    #[test]
    fn test_find_hll_hwnd_does_not_panic() {
        invalidate_hwnd_cache();
        // Returns Some if HLL is running, None otherwise — just verify no panic
        let _result = find_hll_hwnd();
    }

    #[test]
    fn test_send_keys_does_not_error() {
        invalidate_hwnd_cache();
        let result = send_keys_to_hll(false);
        assert!(result.is_ok());
    }

    #[test]
    fn test_send_keys_with_escape_does_not_error() {
        invalidate_hwnd_cache();
        let result = send_keys_to_hll(true);
        assert!(result.is_ok());
    }

    #[test]
    fn test_force_focus_does_not_error() {
        invalidate_hwnd_cache();
        let result = force_focus_and_send_key();
        assert!(result.is_ok());
    }

    #[test]
    fn test_minimize_hll_does_not_error() {
        invalidate_hwnd_cache();
        let result = minimize_hll_window();
        assert!(result.is_ok());
    }

    #[test]
    fn test_focus_esprit_seeder_does_not_panic() {
        let result = focus_esprit_seeder();
        assert!(result.is_ok());
    }

    #[test]
    fn test_vk_f13_constant() {
        assert_eq!(VK_F13, 0x7C);
    }

    #[test]
    fn test_hwnd_cache_ttl() {
        assert_eq!(HWND_CACHE_TTL_MS, 1000);
    }
}
