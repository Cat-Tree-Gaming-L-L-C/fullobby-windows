use dioxus::prelude::*;

use crate::state::servers::LAST_STATS_UPDATE;

#[derive(Debug, Clone, PartialEq, Default)]
pub struct StaleInfo {
    pub seconds_ago: u64,
    pub stale: bool,
}

pub static STALE_INFO: GlobalSignal<StaleInfo> = Signal::global(StaleInfo::default);

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_stale_info_default() {
        let info = StaleInfo::default();
        assert_eq!(info.seconds_ago, 0);
        assert!(!info.stale);
    }

    #[test]
    fn test_stale_info_equality() {
        let a = StaleInfo { seconds_ago: 30, stale: false };
        let b = StaleInfo { seconds_ago: 30, stale: false };
        let c = StaleInfo { seconds_ago: 121, stale: true };
        assert_eq!(a, b);
        assert_ne!(a, c);
    }

    #[test]
    fn test_stale_info_clone() {
        let info = StaleInfo { seconds_ago: 60, stale: false };
        let cloned = info.clone();
        assert_eq!(info, cloned);
    }

    #[test]
    fn test_stale_info_debug() {
        let info = StaleInfo { seconds_ago: 150, stale: true };
        let debug = format!("{:?}", info);
        assert!(debug.contains("StaleInfo"));
        assert!(debug.contains("150"));
        assert!(debug.contains("true"));
    }

    #[test]
    fn test_stale_threshold() {
        // The start_stale_timer function uses > 120 as the stale threshold.
        // Verify the data structure can represent both stale and non-stale states.
        let not_stale = StaleInfo { seconds_ago: 120, stale: false };
        assert!(!not_stale.stale);

        let stale = StaleInfo { seconds_ago: 121, stale: true };
        assert!(stale.stale);
    }
}

/// Start the stale-timer background task.
/// Ticks every 5s to keep the "Updated Xs ago" display current.
pub fn start_stale_timer() {
    spawn(async move {
        loop {
            // If the Dioxus runtime has been torn down (app closing / restart),
            // stop the timer to avoid panicking on GlobalSignal access.
            if dioxus::dioxus_core::Runtime::try_current().is_none() {
                tracing::warn!("Stale timer: Dioxus runtime gone, stopping");
                break;
            }

            let last_update = *LAST_STATS_UPDATE.read();
            if last_update == 0 {
                *STALE_INFO.write() = StaleInfo {
                    seconds_ago: 0,
                    stale: false,
                };
                tokio::time::sleep(std::time::Duration::from_secs(5)).await;
                continue;
            }

            let now_ms = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_millis() as u64;
            let age_ms = now_ms.saturating_sub(last_update);
            let age_secs = age_ms / 1000;

            *STALE_INFO.write() = StaleInfo {
                seconds_ago: age_secs,
                stale: age_secs > 120,
            };

            let before = std::time::Instant::now();
            tokio::time::sleep(std::time::Duration::from_secs(5)).await;
            let elapsed = before.elapsed();

            // Re-check after sleep in case the runtime was destroyed while we slept
            if dioxus::dioxus_core::Runtime::try_current().is_none() {
                tracing::warn!("Stale timer: Dioxus runtime gone after wake, stopping");
                break;
            }

            if elapsed > std::time::Duration::from_secs(15) {
                tracing::info!(
                    "System wake detected in stale timer (sleep took {:.0}s instead of 5s)",
                    elapsed.as_secs_f64(),
                );
            }
        }
    });
}
