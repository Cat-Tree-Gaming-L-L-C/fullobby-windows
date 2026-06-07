use tracing::{info, warn};

/// Show a desktop notification using notify-rust.
/// This replaces tauri-plugin-notification.
pub fn show_notification(title: &str, body: &str) {
    match notify_rust::Notification::new()
        .summary(title)
        .body(body)
        .show()
    {
        Ok(_) => info!("Notification shown: {} - {}", title, body),
        Err(e) => warn!("Failed to show notification: {}", e),
    }
}

/// Play the Windows system notification sound (async, non-blocking).
/// Uses MB_ICONEXCLAMATION for an attention-grabbing "exclamation" chime.
pub fn play_notification_sound() {
    use winapi::um::winuser::{MessageBeep, MB_ICONEXCLAMATION};
    unsafe {
        MessageBeep(MB_ICONEXCLAMATION);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_show_notification_does_not_panic() {
        // In CI/test environments, notification may fail silently.
        // The important thing is it doesn't panic.
        show_notification("Test Title", "Test Body");
    }

    #[test]
    fn test_show_notification_empty_strings() {
        show_notification("", "");
    }

    #[test]
    fn test_show_notification_special_characters() {
        show_notification("Test — Title", "Body with <html> & \"quotes\"");
    }

    #[test]
    fn test_play_notification_sound_does_not_panic() {
        play_notification_sound();
    }
}
