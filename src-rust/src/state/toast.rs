use dioxus::prelude::*;
use std::sync::atomic::{AtomicU64, Ordering};

#[derive(Debug, Clone, PartialEq)]
pub enum ToastType {
    Success,
    Error,
    Info,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Toast {
    pub id: u64,
    pub message: String,
    pub toast_type: ToastType,
}

static NEXT_ID: AtomicU64 = AtomicU64::new(0);

pub static TOASTS: GlobalSignal<Vec<Toast>> = Signal::global(Vec::new);

pub fn add_toast(message: &str, toast_type: ToastType, duration_ms: Option<u64>) {
    let duration = duration_ms.unwrap_or(match toast_type {
        ToastType::Error => 10000,
        _ => 3000,
    });

    // Deduplicate: if an identical message+type toast already exists, dismiss
    // the old one and replace it (effectively restarting its timer).
    {
        let existing = TOASTS.read();
        if let Some(dup) = existing.iter().find(|t| t.message == message && t.toast_type == toast_type) {
            let old_id = dup.id;
            drop(existing);
            dismiss_toast(old_id);
        }
    }

    let id = NEXT_ID.fetch_add(1, Ordering::Relaxed);

    TOASTS.write().push(Toast {
        id,
        message: message.to_string(),
        toast_type,
    });

    // Auto-dismiss after duration
    spawn(async move {
        tokio::time::sleep(std::time::Duration::from_millis(duration)).await;
        dismiss_toast(id);
    });
}

pub fn dismiss_toast(id: u64) {
    TOASTS.write().retain(|t| t.id != id);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_toast_type_equality() {
        assert_eq!(ToastType::Success, ToastType::Success);
        assert_eq!(ToastType::Error, ToastType::Error);
        assert_eq!(ToastType::Info, ToastType::Info);
        assert_ne!(ToastType::Success, ToastType::Error);
        assert_ne!(ToastType::Error, ToastType::Info);
        assert_ne!(ToastType::Success, ToastType::Info);
    }

    #[test]
    fn test_toast_type_clone() {
        let t = ToastType::Error;
        let cloned = t.clone();
        assert_eq!(t, cloned);
    }

    #[test]
    fn test_toast_type_debug() {
        assert_eq!(format!("{:?}", ToastType::Success), "Success");
        assert_eq!(format!("{:?}", ToastType::Error), "Error");
        assert_eq!(format!("{:?}", ToastType::Info), "Info");
    }

    #[test]
    fn test_toast_construction() {
        let toast = Toast {
            id: 42,
            message: "Hello".to_string(),
            toast_type: ToastType::Success,
        };
        assert_eq!(toast.id, 42);
        assert_eq!(toast.message, "Hello");
        assert_eq!(toast.toast_type, ToastType::Success);
    }

    #[test]
    fn test_toast_equality() {
        let a = Toast {
            id: 1,
            message: "msg".to_string(),
            toast_type: ToastType::Info,
        };
        let b = Toast {
            id: 1,
            message: "msg".to_string(),
            toast_type: ToastType::Info,
        };
        let c = Toast {
            id: 2,
            message: "msg".to_string(),
            toast_type: ToastType::Info,
        };
        assert_eq!(a, b);
        assert_ne!(a, c);
    }

    #[test]
    fn test_toast_clone() {
        let toast = Toast {
            id: 99,
            message: "Clone test".to_string(),
            toast_type: ToastType::Error,
        };
        let cloned = toast.clone();
        assert_eq!(toast, cloned);
    }
}
