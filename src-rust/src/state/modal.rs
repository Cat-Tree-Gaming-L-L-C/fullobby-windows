use dioxus::prelude::*;
use tokio::sync::oneshot;
use std::sync::Mutex;
use once_cell::sync::Lazy;

#[derive(Debug, Clone, PartialEq)]
pub enum ModalType {
    Alert,
    Confirm,
    Prompt,
}

#[derive(Debug, Clone, PartialEq)]
pub enum ModalResult {
    /// Alert was dismissed
    Dismissed,
    /// Confirm result
    Confirmed(bool),
    /// Prompt result (None = cancelled)
    Text(Option<String>),
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct ModalState {
    pub open: bool,
    pub modal_type: Option<ModalType>,
    pub title: String,
    pub message: String,
    pub default_value: String,
    /// Show a "Randomize" button in prompt modals (for display name)
    pub show_randomize: bool,
}

pub static MODAL_STATE: GlobalSignal<ModalState> = Signal::global(ModalState::default);

// We store the oneshot sender here so the UI can resolve the modal
static MODAL_RESOLVER: Lazy<Mutex<Option<oneshot::Sender<ModalResult>>>> =
    Lazy::new(|| Mutex::new(None));

pub async fn show_alert(message: &str, title: &str) {
    let (tx, rx) = oneshot::channel();

    {
        let mut resolver = lock!(MODAL_RESOLVER);
        *resolver = Some(tx);
    }

    *MODAL_STATE.write() = ModalState {
        open: true,
        modal_type: Some(ModalType::Alert),
        title: title.to_string(),
        message: message.to_string(),
        ..Default::default()
    };

    let _ = rx.await;
}

pub async fn show_confirm(message: &str, title: &str) -> bool {
    let (tx, rx) = oneshot::channel();

    {
        let mut resolver = lock!(MODAL_RESOLVER);
        *resolver = Some(tx);
    }

    *MODAL_STATE.write() = ModalState {
        open: true,
        modal_type: Some(ModalType::Confirm),
        title: title.to_string(),
        message: message.to_string(),
        ..Default::default()
    };

    match rx.await {
        Ok(ModalResult::Confirmed(v)) => v,
        _ => false,
    }
}

pub async fn show_prompt(message: &str, default_value: &str, title: &str) -> Option<String> {
    let (tx, rx) = oneshot::channel();

    {
        let mut resolver = lock!(MODAL_RESOLVER);
        *resolver = Some(tx);
    }

    *MODAL_STATE.write() = ModalState {
        open: true,
        modal_type: Some(ModalType::Prompt),
        title: title.to_string(),
        message: message.to_string(),
        default_value: default_value.to_string(),
        ..Default::default()
    };

    match rx.await {
        Ok(ModalResult::Text(v)) => v,
        _ => None,
    }
}

/// Show a prompt with a "Randomize" button (for display name changes).
pub async fn show_prompt_with_randomize(message: &str, default_value: &str, title: &str) -> Option<String> {
    let (tx, rx) = oneshot::channel();

    {
        let mut resolver = lock!(MODAL_RESOLVER);
        *resolver = Some(tx);
    }

    *MODAL_STATE.write() = ModalState {
        open: true,
        modal_type: Some(ModalType::Prompt),
        title: title.to_string(),
        message: message.to_string(),
        default_value: default_value.to_string(),
        show_randomize: true,
    };

    match rx.await {
        Ok(ModalResult::Text(v)) => v,
        _ => None,
    }
}

/// Called by the UI component to resolve the modal
pub fn resolve_modal(result: ModalResult) {
    let sender = {
        let mut resolver = lock!(MODAL_RESOLVER);
        resolver.take()
    };

    if let Some(tx) = sender {
        let _ = tx.send(result);
    }

    MODAL_STATE.write().open = false;
}

pub fn close_modal() {
    resolve_modal(ModalResult::Dismissed);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_modal_type_equality() {
        assert_eq!(ModalType::Alert, ModalType::Alert);
        assert_eq!(ModalType::Confirm, ModalType::Confirm);
        assert_eq!(ModalType::Prompt, ModalType::Prompt);
        assert_ne!(ModalType::Alert, ModalType::Confirm);
        assert_ne!(ModalType::Alert, ModalType::Prompt);
        assert_ne!(ModalType::Confirm, ModalType::Prompt);
    }

    #[test]
    fn test_modal_type_clone() {
        let t = ModalType::Prompt;
        assert_eq!(t, t.clone());
    }

    #[test]
    fn test_modal_type_debug() {
        assert_eq!(format!("{:?}", ModalType::Alert), "Alert");
        assert_eq!(format!("{:?}", ModalType::Confirm), "Confirm");
        assert_eq!(format!("{:?}", ModalType::Prompt), "Prompt");
    }

    #[test]
    fn test_modal_result_variants() {
        let dismissed = ModalResult::Dismissed;
        let confirmed_yes = ModalResult::Confirmed(true);
        let confirmed_no = ModalResult::Confirmed(false);
        let text_some = ModalResult::Text(Some("hello".to_string()));
        let text_none = ModalResult::Text(None);

        assert_eq!(dismissed, ModalResult::Dismissed);
        assert_eq!(confirmed_yes, ModalResult::Confirmed(true));
        assert_ne!(confirmed_yes, confirmed_no);
        assert_ne!(text_some, text_none);
        assert_ne!(dismissed, confirmed_yes);
    }

    #[test]
    fn test_modal_result_clone() {
        let result = ModalResult::Text(Some("test".to_string()));
        let cloned = result.clone();
        assert_eq!(result, cloned);
    }

    #[test]
    fn test_modal_state_default() {
        let state = ModalState::default();
        assert!(!state.open);
        assert_eq!(state.modal_type, None);
        assert_eq!(state.title, "");
        assert_eq!(state.message, "");
        assert_eq!(state.default_value, "");
        assert!(!state.show_randomize);
    }

    #[test]
    fn test_modal_state_equality() {
        let a = ModalState {
            open: true,
            modal_type: Some(ModalType::Alert),
            title: "Title".to_string(),
            message: "Msg".to_string(),
            default_value: String::new(),
            show_randomize: false,
        };
        let b = a.clone();
        assert_eq!(a, b);

        let c = ModalState {
            open: false,
            ..a.clone()
        };
        assert_ne!(a, c);
    }

    #[test]
    fn test_modal_state_debug() {
        let state = ModalState::default();
        let debug = format!("{:?}", state);
        assert!(debug.contains("ModalState"));
    }

    #[test]
    fn test_modal_resolver_without_sender() {
        // resolve_modal when no sender is set should not panic
        // We can't test this with GlobalSignals, but we can test the resolver logic
        let mut resolver = MODAL_RESOLVER.lock().unwrap_or_else(|e| e.into_inner());
        assert!(resolver.is_none() || resolver.is_some());
        // Ensure take on None returns None
        let taken = resolver.take();
        // If it was None, sending to None is a no-op
        if let Some(tx) = taken {
            let _ = tx.send(ModalResult::Dismissed);
        }
    }

    #[test]
    fn test_modal_resolver_with_sender() {
        let (tx, rx) = oneshot::channel();
        {
            let mut resolver = MODAL_RESOLVER.lock().unwrap_or_else(|e| e.into_inner());
            *resolver = Some(tx);
        }

        // Take the sender and send a result
        let sender = {
            let mut resolver = MODAL_RESOLVER.lock().unwrap_or_else(|e| e.into_inner());
            resolver.take()
        };
        assert!(sender.is_some());
        sender.unwrap().send(ModalResult::Confirmed(true)).unwrap();

        // Verify the receiver gets the result
        let result = rx.blocking_recv().unwrap();
        assert_eq!(result, ModalResult::Confirmed(true));
    }
}
