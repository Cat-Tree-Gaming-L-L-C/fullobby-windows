use std::collections::HashMap;
use std::sync::RwLock;

use log::debug;
use once_cell::sync::Lazy;
use tokio::task::block_in_place;
use winapi::um::handleapi::CloseHandle;
use winapi::um::processthreadsapi::{OpenProcess, TerminateProcess};
use winapi::um::tlhelp32::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
};
use winapi::um::winbase::QueryFullProcessImageNameW;

use crate::backend::game::GameDefinition;

/// Cached process check result with timestamp
struct CachedProcessCheck {
    timestamp: std::time::Instant,
    is_running: bool,
    pid: Option<u32>,
}

// Cache for process check results
static PROCESS_CACHE: Lazy<RwLock<HashMap<String, CachedProcessCheck>>> =
    Lazy::new(|| RwLock::new(HashMap::new()));

const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
const PROCESS_TERMINATE: u32 = 0x0001;
const PROCESS_CACHE_TTL_MS: u128 = 15000;
const MAX_PROCESS_CACHE_ENTRIES: usize = 10;

/// Check if a PID is still valid using lightweight Windows API call.
fn is_pid_valid(pid: u32) -> bool {
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if handle.is_null() {
            let error = std::io::Error::last_os_error().raw_os_error().unwrap_or(0);
            return error == 5;
        }
        CloseHandle(handle);
        true
    }
}

/// Take a Toolhelp32 snapshot and find all PIDs matching any of the given exe names.
/// Names are matched case-insensitively (ASCII lowering).
fn snapshot_find_by_names(exe_names: &[&str]) -> HashMap<String, Vec<u32>> {
    let mut result: HashMap<String, Vec<u32>> = HashMap::new();

    unsafe {
        let snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if snapshot == winapi::um::handleapi::INVALID_HANDLE_VALUE {
            return result;
        }

        let mut entry: PROCESSENTRY32W = std::mem::zeroed();
        entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;

        if Process32FirstW(snapshot, &mut entry) != 0 {
            loop {
                // Convert szExeFile (null-terminated UTF-16) to a lowercase String
                let name_len = entry
                    .szExeFile
                    .iter()
                    .position(|&c| c == 0)
                    .unwrap_or(entry.szExeFile.len());
                let name = String::from_utf16_lossy(&entry.szExeFile[..name_len]).to_ascii_lowercase();

                // Compare on-the-fly against targets (avoids pre-allocating a Vec of lowercased names)
                if let Some(pos) = exe_names.iter().position(|t| t.eq_ignore_ascii_case(&name)) {
                    let key = exe_names[pos].to_ascii_lowercase();
                    result.entry(key).or_default().push(entry.th32ProcessID);
                }

                if Process32NextW(snapshot, &mut entry) == 0 {
                    break;
                }
            }
        }

        CloseHandle(snapshot);
    }

    result
}

/// Find all PIDs for a single exe name.
fn snapshot_find_by_name(name: &str) -> Vec<u32> {
    let map = snapshot_find_by_names(&[name]);
    map.into_values().next().unwrap_or_default()
}

/// Get the full image path for a process by PID.
/// Returns None if the process can't be opened (e.g. elevated/system processes).
fn get_process_image_path(pid: u32) -> Option<String> {
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if handle.is_null() {
            return None;
        }

        let mut buf = [0u16; 1024];
        let mut size = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(handle, 0, buf.as_mut_ptr(), &mut size);
        CloseHandle(handle);

        if ok == 0 {
            return None;
        }

        Some(String::from_utf16_lossy(&buf[..size as usize]))
    }
}

/// Check if a process is running by exact name using Toolhelp32 snapshot.
pub fn is_process_running(process_name: &str) -> bool {
    let now = std::time::Instant::now();

    // Check cache first (read lock)
    {
        let cache = read_lock!(PROCESS_CACHE);
        if let Some(cached) = cache.get(process_name) {
            if now.duration_since(cached.timestamp).as_millis() < PROCESS_CACHE_TTL_MS {
                if let Some(pid) = cached.pid {
                    let still_running = is_pid_valid(pid);
                    if still_running == cached.is_running {
                        return cached.is_running;
                    }
                    debug!("PID {} state changed for '{}': cached={}, actual={}",
                        pid, process_name, cached.is_running, still_running);
                } else if !cached.is_running {
                    return false;
                }
            }
        }
    }

    // Cache miss — do actual process scan via Toolhelp32
    let pids = block_in_place(|| snapshot_find_by_name(process_name));
    let found = !pids.is_empty();
    let pid = pids.into_iter().next();

    // Update cache (write lock)
    {
        let mut cache = write_lock!(PROCESS_CACHE);
        if cache.len() >= MAX_PROCESS_CACHE_ENTRIES {
            if let Some(oldest_key) = cache
                .iter()
                .min_by_key(|(_, v)| v.timestamp)
                .map(|(k, _)| k.clone())
            {
                cache.remove(&oldest_key);
            }
        }
        cache.insert(process_name.to_string(), CachedProcessCheck {
            timestamp: now,
            is_running: found,
            pid,
        });
    }

    found
}

/// Fresh process scan for `steam.exe`, bypassing the 15s cache.
pub fn is_steam_running_fresh() -> bool {
    let now = std::time::Instant::now();
    let pids = block_in_place(|| snapshot_find_by_name("steam.exe"));
    let found = !pids.is_empty();
    let pid = pids.into_iter().next();

    let mut cache = write_lock!(PROCESS_CACHE);
    cache.insert("steam.exe".to_string(), CachedProcessCheck {
        timestamp: now,
        is_running: found,
        pid,
    });

    found
}

/// Stricter fallback path validation
fn is_hll_steam_path(path_lower: &str) -> bool {
    path_lower.contains("\\steamapps\\common\\hell let loose\\")
        || path_lower.contains("/steamapps/common/hell let loose/")
}

/// Kill all processes matching the given name, verifying they belong to the HLL Steam installation.
pub fn kill_processes_by_name(process_name: &str) {
    let pids = block_in_place(|| snapshot_find_by_name(process_name));

    for pid in pids {
        let exe_path = match get_process_image_path(pid) {
            Some(p) => p,
            None => {
                debug!("Skipping PID {} - path unavailable", pid);
                continue;
            }
        };
        let path_str = exe_path.to_lowercase();
        let is_verified = if let Some(steam_path) = crate::backend::steam::STEAM_PATH_CACHE.get() {
            if let Some(steam_dir) = steam_path.parent().and_then(|p| p.parent()) {
                let steam_dir_lower = steam_dir.to_string_lossy().to_lowercase();
                path_str.starts_with(&steam_dir_lower) && path_str.contains("hell let loose")
            } else {
                is_hll_steam_path(&path_str)
            }
        } else {
            is_hll_steam_path(&path_str)
        };

        if is_verified {
            debug!("Terminating verified game process (PID {})", pid);
            unsafe {
                let handle = OpenProcess(PROCESS_TERMINATE, 0, pid);
                if !handle.is_null() {
                    TerminateProcess(handle, 1);
                    CloseHandle(handle);
                } else {
                    debug!("Failed to open process {} for termination", pid);
                }
            }
        } else if path_str.is_empty() {
            debug!("Skipping PID {} - path unavailable", pid);
        } else {
            debug!("Skipping PID {} - unverified path", pid);
        }
    }

    // Invalidate cache for this process
    {
        let mut cache = write_lock!(PROCESS_CACHE);
        cache.remove(process_name);
    }
}

pub fn is_game_loading() -> bool {
    is_process_running("Launch_HLL.exe")
}

/// Check EAC bootstrapper and HLL process with a single fresh snapshot.
pub fn check_launch_processes() -> (bool, bool) {
    let now = std::time::Instant::now();
    let map = block_in_place(|| {
        snapshot_find_by_names(&["Launch_HLL.exe", "HLL-Win64-Shipping.exe"])
    });

    let eac_pids = map.get("launch_hll.exe");
    let eac_found = eac_pids.map(|v| !v.is_empty()).unwrap_or(false);
    let eac_pid = eac_pids.and_then(|v| v.first().copied());

    let hll_pids = map.get("hll-win64-shipping.exe");
    let hll_found = hll_pids.map(|v| !v.is_empty()).unwrap_or(false);
    let hll_pid = hll_pids.and_then(|v| v.first().copied());

    let mut cache = write_lock!(PROCESS_CACHE);
    cache.insert("Launch_HLL.exe".to_string(), CachedProcessCheck {
        timestamp: now,
        is_running: eac_found,
        pid: eac_pid,
    });
    cache.insert("HLL-Win64-Shipping.exe".to_string(), CachedProcessCheck {
        timestamp: now,
        is_running: hll_found,
        pid: hll_pid,
    });

    (eac_found, hll_found)
}

// ============================================================================
// GAME-AWARE FUNCTIONS (parameterized by GameDefinition)
// ============================================================================

/// Check if a specific game's main process is running.
pub fn is_game_running(game: &GameDefinition) -> bool {
    is_process_running(game.exe_name)
}

/// Check if a game's EAC launcher is running.
pub fn is_game_loading_for(game: &GameDefinition) -> bool {
    is_process_running(game.launcher_exe_name)
}

/// Check EAC bootstrapper and game process with a single fresh snapshot.
pub fn check_game_launch_processes(game: &GameDefinition) -> (bool, bool) {
    let now = std::time::Instant::now();
    let launcher_lower = game.launcher_exe_name.to_ascii_lowercase();
    let exe_lower = game.exe_name.to_ascii_lowercase();

    let map = block_in_place(|| {
        snapshot_find_by_names(&[game.launcher_exe_name, game.exe_name])
    });

    let eac_pids = map.get(&launcher_lower);
    let eac_found = eac_pids.map(|v| !v.is_empty()).unwrap_or(false);
    let eac_pid = eac_pids.and_then(|v| v.first().copied());

    let exe_pids = map.get(&exe_lower);
    let exe_found = exe_pids.map(|v| !v.is_empty()).unwrap_or(false);
    let exe_pid = exe_pids.and_then(|v| v.first().copied());

    let mut cache = write_lock!(PROCESS_CACHE);
    cache.insert(game.launcher_exe_name.to_string(), CachedProcessCheck {
        timestamp: now,
        is_running: eac_found,
        pid: eac_pid,
    });
    cache.insert(game.exe_name.to_string(), CachedProcessCheck {
        timestamp: now,
        is_running: exe_found,
        pid: exe_pid,
    });

    (eac_found, exe_found)
}

/// Kill all processes matching the game's exe name, verifying they belong to the Steam installation.
pub fn kill_game_processes(game: &GameDefinition) {
    let pids = block_in_place(|| snapshot_find_by_name(game.exe_name));

    for pid in pids {
        let exe_path = match get_process_image_path(pid) {
            Some(p) => p,
            None => {
                debug!("Skipping PID {} - path unavailable", pid);
                continue;
            }
        };
        let path_str = exe_path.to_lowercase();
        let folder_lower = game.install_folder.to_ascii_lowercase();

        let is_verified = if let Some(steam_path) = crate::backend::steam::STEAM_PATH_CACHE.get() {
            if let Some(steam_dir) = steam_path.parent().and_then(|p| p.parent()) {
                let steam_dir_lower = steam_dir.to_string_lossy().to_lowercase();
                path_str.starts_with(&steam_dir_lower) && path_str.contains(&folder_lower)
            } else {
                is_game_steam_path(&path_str, &folder_lower)
            }
        } else {
            is_game_steam_path(&path_str, &folder_lower)
        };

        if is_verified {
            debug!("Terminating verified game process (PID {})", pid);
            unsafe {
                let handle = OpenProcess(PROCESS_TERMINATE, 0, pid);
                if !handle.is_null() {
                    TerminateProcess(handle, 1);
                    CloseHandle(handle);
                } else {
                    debug!("Failed to open process {} for termination", pid);
                }
            }
        } else if path_str.is_empty() {
            debug!("Skipping PID {} - path unavailable", pid);
        } else {
            debug!("Skipping PID {} - unverified path", pid);
        }
    }

    // Invalidate cache for this process
    {
        let mut cache = write_lock!(PROCESS_CACHE);
        cache.remove(game.exe_name);
    }
}

fn is_game_steam_path(path_lower: &str, install_folder_lower: &str) -> bool {
    (path_lower.contains("\\steamapps\\common\\") || path_lower.contains("/steamapps/common/"))
        && path_lower.contains(install_folder_lower)
}

#[cfg(test)]
mod tests {
    use super::*;

    // ─── is_hll_steam_path ──────────────────────────────────────────

    #[test]
    fn test_is_hll_steam_path_backslash() {
        assert!(is_hll_steam_path(
            r"c:\program files (x86)\steam\steamapps\common\hell let loose\hll-win64-shipping.exe"
        ));
    }

    #[test]
    fn test_is_hll_steam_path_forward_slash() {
        assert!(is_hll_steam_path(
            "c:/program files (x86)/steam/steamapps/common/hell let loose/hll-win64-shipping.exe"
        ));
    }

    #[test]
    fn test_is_hll_steam_path_non_steam() {
        assert!(!is_hll_steam_path(
            r"c:\games\hell let loose\hll-win64-shipping.exe"
        ));
    }

    #[test]
    fn test_is_hll_steam_path_wrong_game() {
        assert!(!is_hll_steam_path(
            r"c:\steam\steamapps\common\counter-strike\cs.exe"
        ));
    }

    #[test]
    fn test_is_hll_steam_path_empty() {
        assert!(!is_hll_steam_path(""));
    }

    // ─── is_game_steam_path ─────────────────────────────────────────

    #[test]
    fn test_is_game_steam_path_hll() {
        assert!(is_game_steam_path(
            r"c:\steam\steamapps\common\hell let loose\hll.exe",
            "hell let loose"
        ));
    }

    #[test]
    fn test_is_game_steam_path_hllv() {
        assert!(is_game_steam_path(
            r"c:\steam\steamapps\common\hllv\hllv.exe",
            "hllv"
        ));
    }

    #[test]
    fn test_is_game_steam_path_forward_slash() {
        assert!(is_game_steam_path(
            "c:/steam/steamapps/common/hell let loose/hll.exe",
            "hell let loose"
        ));
    }

    #[test]
    fn test_is_game_steam_path_wrong_game() {
        assert!(!is_game_steam_path(
            r"c:\steam\steamapps\common\counter-strike\cs.exe",
            "hell let loose"
        ));
    }

    #[test]
    fn test_is_game_steam_path_non_steam() {
        assert!(!is_game_steam_path(
            r"c:\games\hell let loose\hll.exe",
            "hell let loose"
        ));
    }
}
