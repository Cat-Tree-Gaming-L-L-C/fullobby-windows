use once_cell::sync::OnceCell;
use serde::{Deserialize, Serialize};
use tokio::sync::broadcast;

/// Typed event bus replacing Tauri's `app.emit()` / `listen()`.
/// Backend modules send events via `send_event()`, the UI subscribes via `subscribe()`.

/// All application events that flow from backend to frontend.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub enum AppEvent {
    // Splash bypass events
    SplashBypassStarted {
        duration_secs: u64,
    },
    SplashBypassComplete,
    SplashBypassTimeout {
        reason: String,
    },

    // Game lifecycle
    HllClosed,

    // Autoseed countdown events
    AutoseedCountdownStarted {
        duration_secs: u64,
        seed_type: String,
    },
    AutoseedCountdownComplete,
    AutoseedCountdownCancelled,

    // Server switch events
    ServerSwitchPending {
        countdown_secs: u64,
        server_name: String,
        reason: String,
    },
    ServerSwitchSnoozed {
        snooze_secs: u64,
    },
    ServerSwitchExecuting,
    ServerSwitchCancelled,

    // Autoseed monitor/rotation events
    AutoseedSeedingStarted {
        server_index: usize,
        region: String,
        server_name: String,
    },
    AutoseedServerRestartFailed {
        server_name: String,
        reason: String,
    },

    // Launch watcher events (game update recovery)
    SeedingUpdateWaiting {
        server_index: usize,
        region: String,
    },
    SeedingUpdateStarted {
        server_index: usize,
        region: String,
    },
    SeedingUpdateTimeout,

    // Single instance
    SingleInstance {
        args: Vec<String>,
    },

    // Player name reset
    PlayerNameReset,
}

/// Channel capacity — large enough that slow UI consumers don't cause
/// the backend to lose events, but bounded to prevent unbounded memory growth.
const EVENT_CHANNEL_CAPACITY: usize = 256;

static EVENT_TX: OnceCell<broadcast::Sender<AppEvent>> = OnceCell::new();

/// Initialize the event bus. Safe to call multiple times — only the first call has effect.
pub fn init_event_bus() {
    EVENT_TX.get_or_init(|| {
        let (tx, _rx) = broadcast::channel(EVENT_CHANNEL_CAPACITY);
        tx
    });
}

/// Send an event from backend code. Safe to call from any thread/task.
pub fn send_event(event: AppEvent) {
    if let Some(tx) = EVENT_TX.get() {
        // Ignore send errors (no subscribers = no one listening)
        let _ = tx.send(event);
    } else {
        tracing::warn!("Event bus not initialized, dropping event: {:?}", event);
    }
}

/// Subscribe to the event stream. Returns a broadcast receiver.
/// Call from UI components or coroutines that need to react to backend events.
pub fn subscribe() -> broadcast::Receiver<AppEvent> {
    EVENT_TX
        .get()
        .expect("Event bus not initialized — call init_event_bus() first")
        .subscribe()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn test_event_bus_send_receive() {
        // Idempotent init — safe even if another test already called it
        init_event_bus();
        init_event_bus(); // second call should not panic

        let mut rx = subscribe();

        send_event(AppEvent::SplashBypassComplete);

        let event = rx.recv().await.expect("should receive event");
        match event {
            AppEvent::SplashBypassComplete => {} // expected
            other => panic!("unexpected event: {:?}", other),
        }
    }

    #[tokio::test]
    async fn test_event_bus_multiple_events() {
        init_event_bus();
        let mut rx = subscribe();

        send_event(AppEvent::SplashBypassStarted { duration_secs: 30 });
        send_event(AppEvent::SplashBypassComplete);
        send_event(AppEvent::HllClosed);

        let e1 = rx.recv().await.unwrap();
        assert!(matches!(e1, AppEvent::SplashBypassStarted { duration_secs: 30 }));

        let e2 = rx.recv().await.unwrap();
        assert!(matches!(e2, AppEvent::SplashBypassComplete));

        let e3 = rx.recv().await.unwrap();
        assert!(matches!(e3, AppEvent::HllClosed));
    }

    #[tokio::test]
    async fn test_event_bus_multiple_subscribers() {
        init_event_bus();
        let mut rx1 = subscribe();
        let mut rx2 = subscribe();

        send_event(AppEvent::PlayerNameReset);

        let e1 = rx1.recv().await.unwrap();
        let e2 = rx2.recv().await.unwrap();
        assert!(matches!(e1, AppEvent::PlayerNameReset));
        assert!(matches!(e2, AppEvent::PlayerNameReset));
    }

    #[test]
    fn test_event_serde_roundtrip_splash_bypass_started() {
        let event = AppEvent::SplashBypassStarted { duration_secs: 45 };
        let json = serde_json::to_string(&event).unwrap();
        let deserialized: AppEvent = serde_json::from_str(&json).unwrap();
        assert!(matches!(deserialized, AppEvent::SplashBypassStarted { duration_secs: 45 }));
    }

    #[test]
    fn test_event_serde_roundtrip_splash_bypass_timeout() {
        let event = AppEvent::SplashBypassTimeout { reason: "timed out".to_string() };
        let json = serde_json::to_string(&event).unwrap();
        let deserialized: AppEvent = serde_json::from_str(&json).unwrap();
        match deserialized {
            AppEvent::SplashBypassTimeout { reason } => assert_eq!(reason, "timed out"),
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn test_event_serde_roundtrip_autoseed_countdown_started() {
        let event = AppEvent::AutoseedCountdownStarted {
            duration_secs: 60,
            seed_type: "na".to_string(),
        };
        let json = serde_json::to_string(&event).unwrap();
        let deserialized: AppEvent = serde_json::from_str(&json).unwrap();
        match deserialized {
            AppEvent::AutoseedCountdownStarted { duration_secs, seed_type } => {
                assert_eq!(duration_secs, 60);
                assert_eq!(seed_type, "na");
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn test_event_serde_roundtrip_server_switch_pending() {
        let event = AppEvent::ServerSwitchPending {
            countdown_secs: 30,
            server_name: "Esprit PF".to_string(),
            reason: "population change".to_string(),
        };
        let json = serde_json::to_string(&event).unwrap();
        let deserialized: AppEvent = serde_json::from_str(&json).unwrap();
        match deserialized {
            AppEvent::ServerSwitchPending { countdown_secs, server_name, reason } => {
                assert_eq!(countdown_secs, 30);
                assert_eq!(server_name, "Esprit PF");
                assert_eq!(reason, "population change");
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn test_event_serde_roundtrip_autoseed_seeding_started() {
        let event = AppEvent::AutoseedSeedingStarted {
            server_index: 2,
            region: "eu".to_string(),
            server_name: "EU Server".to_string(),
        };
        let json = serde_json::to_string(&event).unwrap();
        let deserialized: AppEvent = serde_json::from_str(&json).unwrap();
        match deserialized {
            AppEvent::AutoseedSeedingStarted { server_index, region, server_name } => {
                assert_eq!(server_index, 2);
                assert_eq!(region, "eu");
                assert_eq!(server_name, "EU Server");
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn test_event_serde_roundtrip_single_instance() {
        let event = AppEvent::SingleInstance {
            args: vec!["--autoseed-na".to_string(), "--verbose".to_string()],
        };
        let json = serde_json::to_string(&event).unwrap();
        let deserialized: AppEvent = serde_json::from_str(&json).unwrap();
        match deserialized {
            AppEvent::SingleInstance { args } => {
                assert_eq!(args.len(), 2);
                assert_eq!(args[0], "--autoseed-na");
                assert_eq!(args[1], "--verbose");
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn test_event_serde_roundtrip_unit_variants() {
        for event in [
            AppEvent::SplashBypassComplete,
            AppEvent::HllClosed,
            AppEvent::AutoseedCountdownComplete,
            AppEvent::AutoseedCountdownCancelled,
            AppEvent::ServerSwitchExecuting,
            AppEvent::ServerSwitchCancelled,
            AppEvent::SeedingUpdateTimeout,
            AppEvent::PlayerNameReset,
        ] {
            let json = serde_json::to_string(&event).unwrap();
            let _deserialized: AppEvent = serde_json::from_str(&json).unwrap();
        }
    }

    #[test]
    fn test_event_serde_roundtrip_seeding_update_waiting() {
        let event = AppEvent::SeedingUpdateWaiting {
            server_index: 0,
            region: "na".to_string(),
        };
        let json = serde_json::to_string(&event).unwrap();
        let deserialized: AppEvent = serde_json::from_str(&json).unwrap();
        match deserialized {
            AppEvent::SeedingUpdateWaiting { server_index, region } => {
                assert_eq!(server_index, 0);
                assert_eq!(region, "na");
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn test_event_serde_roundtrip_server_switch_snoozed() {
        let event = AppEvent::ServerSwitchSnoozed { snooze_secs: 120 };
        let json = serde_json::to_string(&event).unwrap();
        let deserialized: AppEvent = serde_json::from_str(&json).unwrap();
        match deserialized {
            AppEvent::ServerSwitchSnoozed { snooze_secs } => assert_eq!(snooze_secs, 120),
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn test_event_serde_roundtrip_autoseed_restart_failed() {
        let event = AppEvent::AutoseedServerRestartFailed {
            server_name: "Server A".to_string(),
            reason: "timeout".to_string(),
        };
        let json = serde_json::to_string(&event).unwrap();
        let deserialized: AppEvent = serde_json::from_str(&json).unwrap();
        match deserialized {
            AppEvent::AutoseedServerRestartFailed { server_name, reason } => {
                assert_eq!(server_name, "Server A");
                assert_eq!(reason, "timeout");
            }
            _ => panic!("wrong variant"),
        }
    }

    #[test]
    fn test_event_debug_format() {
        let event = AppEvent::HllClosed;
        let debug = format!("{:?}", event);
        assert!(debug.contains("HllClosed"));
    }

    #[test]
    fn test_event_clone() {
        let event = AppEvent::ServerSwitchPending {
            countdown_secs: 10,
            server_name: "Test".to_string(),
            reason: "low pop".to_string(),
        };
        let cloned = event.clone();
        let json1 = serde_json::to_string(&event).unwrap();
        let json2 = serde_json::to_string(&cloned).unwrap();
        assert_eq!(json1, json2);
    }
}
