use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{Duration, Instant};

static COOLDOWNS: Mutex<Option<HashMap<String, Instant>>> = Mutex::new(None);

fn with_map<R>(f: impl FnOnce(&mut HashMap<String, Instant>) -> R) -> R {
    let mut guard = COOLDOWNS.lock().unwrap_or_else(|e| e.into_inner());
    let map = guard.get_or_insert_with(HashMap::new);
    f(map)
}

/// Record a cooldown for `key` lasting `secs` seconds.
pub fn set_cooldown(key: &str, secs: u64) {
    with_map(|m| {
        m.insert(key.to_string(), Instant::now() + Duration::from_secs(secs));
    });
}

/// Returns `true` if `key` is still on cooldown.
pub fn is_on_cooldown(key: &str) -> bool {
    with_map(|m| {
        m.get(key)
            .map(|until| Instant::now() < *until)
            .unwrap_or(false)
    })
}

/// Seconds remaining on `key`'s cooldown (0 if expired or unset).
pub fn remaining_secs(key: &str) -> u64 {
    with_map(|m| {
        m.get(key)
            .map(|until| until.saturating_duration_since(Instant::now()).as_secs())
            .unwrap_or(0)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_cooldown_lifecycle() {
        // Unset key: not on cooldown
        assert!(!is_on_cooldown("test_unset_key_unique"));
        assert_eq!(remaining_secs("test_unset_key_unique"), 0);

        // Set 3600s: should be on cooldown with >3500s remaining
        set_cooldown("test_cd_3600", 3600);
        assert!(is_on_cooldown("test_cd_3600"));
        assert!(remaining_secs("test_cd_3600") > 3500);

        // Set 0s: immediately expired
        set_cooldown("test_cd_zero", 0);
        assert!(!is_on_cooldown("test_cd_zero"));
        assert_eq!(remaining_secs("test_cd_zero"), 0);

        // Keys are independent
        set_cooldown("test_cd_a", 100);
        assert!(!is_on_cooldown("test_cd_b_independent"));
        assert!(is_on_cooldown("test_cd_a"));

        // Overwrite works
        set_cooldown("test_cd_overwrite", 10);
        set_cooldown("test_cd_overwrite", 7200);
        assert!(remaining_secs("test_cd_overwrite") > 7000);
    }
}

// ─── Reactive cooldown signals ──────────────────────────────────────

use dioxus::prelude::*;

/// Reactive remaining seconds for the seed_all cooldown.
/// Updated via the countdown system — no polling needed.
pub static SEED_ALL_COOLDOWN_REMAINING: GlobalSignal<u64> = Signal::global(|| 0);

/// Set the seed_all cooldown and start a reactive countdown signal.
/// Call this instead of `set_cooldown("seed_all", secs)` from UI code.
pub fn set_seed_all_cooldown(secs: u64) {
    set_cooldown("seed_all", secs);
    *SEED_ALL_COOLDOWN_REMAINING.write() = secs;
    crate::start_countdown_signal!("seed_all_ui", SEED_ALL_COOLDOWN_REMAINING, 0, None::<Box<dyn FnOnce() + Send>>);
}

