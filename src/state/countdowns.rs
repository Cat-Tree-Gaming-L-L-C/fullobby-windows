use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::sync::atomic::{AtomicBool, Ordering};
use once_cell::sync::Lazy;

type ReadFn = Arc<dyn Fn() -> u64 + Send + Sync>;
type WriteFn = Arc<dyn Fn(u64) + Send + Sync>;
type CountdownCallback = Box<dyn FnOnce() + Send + 'static>;

struct CountdownEntry {
    read: ReadFn,
    write: WriteFn,
    min_value: u64,
    on_complete: Option<CountdownCallback>,
}

static ACTIVE_COUNTDOWNS: Lazy<Arc<Mutex<HashMap<String, CountdownEntry>>>> =
    Lazy::new(|| Arc::new(Mutex::new(HashMap::new())));

static TIMER_RUNNING: Lazy<Arc<AtomicBool>> =
    Lazy::new(|| Arc::new(AtomicBool::new(false)));

fn ensure_timer_running() {
    if TIMER_RUNNING.swap(true, Ordering::SeqCst) {
        return; // already running
    }

    let countdowns = ACTIVE_COUNTDOWNS.clone();
    let running = TIMER_RUNNING.clone();

    // Use dioxus::spawn so GlobalSignal read/write happens within the Dioxus
    // runtime context.  tokio::spawn would run outside that context, which
    // panics (and with panic=abort, silently kills the process).
    dioxus::prelude::spawn(async move {
        loop {
            // If the Dioxus runtime has been torn down (app closing / restart),
            // stop the timer to avoid panicking on GlobalSignal access.
            if dioxus::dioxus_core::Runtime::try_current().is_none() {
                tracing::warn!("Countdown timer: Dioxus runtime gone, stopping");
                running.store(false, Ordering::SeqCst);
                break;
            }

            let before = std::time::Instant::now();
            tokio::time::sleep(std::time::Duration::from_secs(1)).await;
            let elapsed = before.elapsed();

            // Calculate how many seconds to subtract (normally 1, more after wake)
            let ticks = if elapsed > std::time::Duration::from_secs(3) {
                let secs = elapsed.as_secs();
                tracing::info!(
                    "System wake detected in countdown timer (sleep took {}s instead of 1s)",
                    secs
                );
                secs
            } else {
                1
            };

            // Re-check after sleep in case the runtime was destroyed while we slept
            if dioxus::dioxus_core::Runtime::try_current().is_none() {
                tracing::warn!("Countdown timer: Dioxus runtime gone after wake, stopping");
                running.store(false, Ordering::SeqCst);
                break;
            }

            let mut completed = Vec::new();
            {
                let mut map = countdowns.lock().unwrap_or_else(|e| e.into_inner());
                if map.is_empty() {
                    running.store(false, Ordering::SeqCst);
                    break;
                }

                for (key, entry) in map.iter() {
                    let current = (entry.read)();
                    let new_val = current.saturating_sub(ticks).max(entry.min_value);
                    (entry.write)(new_val);
                    if new_val <= entry.min_value {
                        completed.push(key.clone());
                    }
                }

                for key in &completed {
                    if let Some(entry) = map.remove(key) {
                        if let Some(callback) = entry.on_complete {
                            callback();
                        }
                    }
                }
            }
        }
    });
}

/// Start a countdown that decrements a GlobalSignal<u64> every second.
///
/// Use like:
/// ```ignore
/// start_countdown_signal!("my-key", SOME_SIGNAL, 0, None);
/// ```
/// where `SOME_SIGNAL` is a `GlobalSignal<u64>`.
#[macro_export]
macro_rules! start_countdown_signal {
    ($key:expr, $signal:expr, $min:expr, $on_complete:expr) => {
        $crate::state::countdowns::start_countdown(
            $key,
            std::sync::Arc::new(|| *$signal.read()),
            std::sync::Arc::new(|v| *$signal.write() = v),
            $min,
            $on_complete,
        )
    };
}

pub fn start_countdown(
    key: &str,
    read: Arc<dyn Fn() -> u64 + Send + Sync>,
    write: Arc<dyn Fn(u64) + Send + Sync>,
    min_value: u64,
    on_complete: Option<Box<dyn FnOnce() + Send + 'static>>,
) {
    let mut map = ACTIVE_COUNTDOWNS.lock().unwrap_or_else(|e| e.into_inner());
    map.insert(
        key.to_string(),
        CountdownEntry {
            read,
            write,
            min_value,
            on_complete,
        },
    );
    drop(map);
    ensure_timer_running();
}

pub fn stop_countdown(key: &str) {
    let mut map = ACTIVE_COUNTDOWNS.lock().unwrap_or_else(|e| e.into_inner());
    map.remove(key);
}

pub fn stop_all_countdowns() {
    let mut map = ACTIVE_COUNTDOWNS.lock().unwrap_or_else(|e| e.into_inner());
    map.clear();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_stop_countdown_nonexistent_key() {
        // Should not panic when stopping a countdown that doesn't exist
        stop_countdown("nonexistent_test_key_12345");
    }

    #[test]
    fn test_stop_all_countdowns_empty() {
        // Should not panic when clearing an empty map
        stop_all_countdowns();
    }

    #[test]
    fn test_start_and_stop_countdown_no_timer() {
        // We can insert entries into the map without spawning the timer
        // by directly manipulating ACTIVE_COUNTDOWNS
        let value = Arc::new(std::sync::atomic::AtomicU64::new(60));
        let val_read = Arc::clone(&value);
        let val_write = Arc::clone(&value);

        {
            let mut map = ACTIVE_COUNTDOWNS.lock().unwrap();
            map.insert(
                "test_entry".to_string(),
                CountdownEntry {
                    read: Arc::new(move || val_read.load(std::sync::atomic::Ordering::Relaxed)),
                    write: Arc::new(move |v| val_write.store(v, std::sync::atomic::Ordering::Relaxed)),
                    min_value: 0,
                    on_complete: None,
                },
            );
            assert!(map.contains_key("test_entry"));
        }

        stop_countdown("test_entry");

        {
            let map = ACTIVE_COUNTDOWNS.lock().unwrap();
            assert!(!map.contains_key("test_entry"));
        }
    }

    #[test]
    fn test_stop_all_clears_multiple() {
        {
            let mut map = ACTIVE_COUNTDOWNS.lock().unwrap();
            for i in 0..3 {
                let key = format!("test_multi_{}", i);
                map.insert(
                    key,
                    CountdownEntry {
                        read: Arc::new(|| 0),
                        write: Arc::new(|_| {}),
                        min_value: 0,
                        on_complete: None,
                    },
                );
            }
        }

        stop_all_countdowns();

        {
            let map = ACTIVE_COUNTDOWNS.lock().unwrap();
            assert!(!map.contains_key("test_multi_0"));
            assert!(!map.contains_key("test_multi_1"));
            assert!(!map.contains_key("test_multi_2"));
        }
    }

    #[test]
    fn test_countdown_entry_on_complete_callback() {
        let called = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let called_clone = Arc::clone(&called);

        let callback: CountdownCallback = Box::new(move || {
            called_clone.store(true, std::sync::atomic::Ordering::Relaxed);
        });

        {
            let mut map = ACTIVE_COUNTDOWNS.lock().unwrap();
            map.insert(
                "test_callback".to_string(),
                CountdownEntry {
                    read: Arc::new(|| 0),
                    write: Arc::new(|_| {}),
                    min_value: 0,
                    on_complete: Some(callback),
                },
            );
        }

        // Remove the entry and invoke its callback manually
        let entry = {
            let mut map = ACTIVE_COUNTDOWNS.lock().unwrap();
            map.remove("test_callback")
        };

        if let Some(entry) = entry {
            if let Some(cb) = entry.on_complete {
                cb();
            }
        }

        assert!(called.load(std::sync::atomic::Ordering::Relaxed));
    }
}
