use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::RwLock;
use std::time::Duration;

use once_cell::sync::Lazy;
use tracing::{info, warn};

/// Whether a heartbeat loop is currently running
static HEARTBEAT_ACTIVE: AtomicBool = AtomicBool::new(false);

/// Signal to stop the heartbeat loop
static HEARTBEAT_STOP: AtomicBool = AtomicBool::new(false);

/// Current session ID being heartbeat-ed
static HEARTBEAT_SESSION_ID: Lazy<RwLock<Option<String>>> = Lazy::new(|| RwLock::new(None));

/// Heartbeat interval in seconds
const HEARTBEAT_INTERVAL_SECS: u64 = 30;

/// Start the heartbeat loop for a seeding session.
/// Spawns a tokio task that sends heartbeats every 30s.
/// If a heartbeat loop is already running, it is stopped first.
pub async fn start_heartbeat(session_id: String) {
    // Stop any existing heartbeat first
    if HEARTBEAT_ACTIVE.load(Ordering::Acquire) {
        HEARTBEAT_STOP.store(true, Ordering::Release);
        // Give the loop a moment to stop
        tokio::time::sleep(Duration::from_millis(100)).await;
    }

    {
        let mut sid = write_lock!(HEARTBEAT_SESSION_ID);
        *sid = Some(session_id.clone());
    }
    HEARTBEAT_STOP.store(false, Ordering::Release);
    HEARTBEAT_ACTIVE.store(true, Ordering::Release);

    tokio::spawn(async move {
        info!("Heartbeat started for session {}", session_id);
        let mut consecutive_failures: u32 = 0;

        loop {
            // Base interval + exponential backoff on consecutive failures (cap 5 min)
            let backoff_secs = if consecutive_failures > 0 {
                let extra = HEARTBEAT_INTERVAL_SECS * (1u64 << consecutive_failures.min(4));
                extra.min(300)
            } else {
                HEARTBEAT_INTERVAL_SECS
            };

            let before = std::time::Instant::now();
            tokio::time::sleep(Duration::from_secs(backoff_secs)).await;
            let elapsed = before.elapsed();

            if HEARTBEAT_STOP.load(Ordering::Acquire) {
                info!("Heartbeat loop stopping (stop signal received)");
                break;
            }

            // If sleep took >3x expected, the system likely slept.
            // Wait 3s for the network stack to reconnect before sending.
            if elapsed > Duration::from_secs(backoff_secs * 3) {
                info!(
                    "System wake detected in heartbeat loop (sleep took {:.0}s instead of {}s), waiting 3s for network",
                    elapsed.as_secs_f64(),
                    backoff_secs
                );
                tokio::time::sleep(Duration::from_secs(3)).await;

                if HEARTBEAT_STOP.load(Ordering::Acquire) {
                    info!("Heartbeat loop stopping after wake (stop signal received)");
                    break;
                }
            }

            match crate::api::client::send_heartbeat(&session_id).await {
                Ok(resp) => {
                    if consecutive_failures > 0 {
                        info!("Heartbeat recovered after {} failures", consecutive_failures);
                    }
                    consecutive_failures = 0;
                    info!(
                        "Heartbeat sent: count={}, duration={}s, validated={}",
                        resp.heartbeat_count, resp.session_duration_secs, resp.validated
                    );
                }
                Err(e) => {
                    consecutive_failures = consecutive_failures.saturating_add(1);
                    if consecutive_failures >= 10 {
                        tracing::error!(
                            "Heartbeat failed ({} consecutive): {}",
                            consecutive_failures, e
                        );
                    } else {
                        warn!(
                            "Heartbeat failed ({} consecutive, next in {}s): {}",
                            consecutive_failures,
                            HEARTBEAT_INTERVAL_SECS * (1u64 << consecutive_failures.min(4)).min(300),
                            e
                        );
                    }
                }
            }
        }

        HEARTBEAT_ACTIVE.store(false, Ordering::Release);
        info!("Heartbeat loop ended");
    });
}

/// Stop the heartbeat loop and notify the API that the session has ended.
/// This is async — call from async contexts.
pub async fn stop_heartbeat(reason: Option<&str>) {
    HEARTBEAT_STOP.store(true, Ordering::Release);

    let session_id = {
        let mut sid = write_lock!(HEARTBEAT_SESSION_ID);
        sid.take()
    };

    if let Some(ref sid) = session_id {
        match crate::api::client::stop_session(sid, reason).await {
            Ok(_) => info!("Seeding session {} stopped (reason: {:?})", sid, reason),
            Err(e) => warn!("Failed to stop session {}: {}", sid, e),
        }
    }

    HEARTBEAT_ACTIVE.store(false, Ordering::Release);
}

/// Stop the heartbeat loop synchronously (fire-and-forget).
/// Use this from non-async contexts — spawns a tokio task to do the actual stop.
pub fn stop_heartbeat_sync(reason: Option<String>) {
    HEARTBEAT_STOP.store(true, Ordering::Release);

    let session_id = {
        let mut sid = write_lock!(HEARTBEAT_SESSION_ID);
        sid.take()
    };

    if let Some(sid) = session_id {
        tokio::spawn(async move {
            match crate::api::client::stop_session(&sid, reason.as_deref()).await {
                Ok(_) => info!("Seeding session {} stopped (sync, reason: {:?})", sid, reason),
                Err(e) => warn!("Failed to stop session {} (sync): {}", sid, e),
            }
        });
    }

    HEARTBEAT_ACTIVE.store(false, Ordering::Release);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_heartbeat_constants() {
        assert_eq!(HEARTBEAT_INTERVAL_SECS, 30);
        // Wake detection threshold is 3x the interval
        assert_eq!(HEARTBEAT_INTERVAL_SECS * 3, 90);
    }
}
