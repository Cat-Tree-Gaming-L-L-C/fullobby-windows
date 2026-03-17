use dioxus::prelude::*;

use crate::state::toast::{dismiss_toast, ToastType, TOASTS};

#[component]
pub fn ToastContainer() -> Element {
    let toasts = TOASTS.read();

    if toasts.is_empty() {
        return rsx! {};
    }

    rsx! {
        div { class: "toast toast-end toast-bottom z-50",
            for toast in toasts.iter() {
                div {
                    key: "{toast.id}",
                    class: match toast.toast_type {
                        ToastType::Success => "alert alert-success",
                        ToastType::Error => "alert alert-error",
                        ToastType::Info => "alert alert-info",
                    },
                    span { "{toast.message}" }
                    button {
                        class: "btn btn-ghost btn-xs opacity-60",
                        aria_label: "Dismiss",
                        onclick: {
                            let id = toast.id;
                            move |_| dismiss_toast(id)
                        },
                        "\u{00D7}"
                    }
                }
            }
        }
    }
}
