use dioxus::prelude::*;
use dioxus::prelude::Key;

use crate::state::modal::{resolve_modal, ModalResult, ModalType, MODAL_STATE};

#[component]
pub fn Modal() -> Element {
    let state = MODAL_STATE.read();

    if !state.open {
        return rsx! {};
    }

    let modal_type = state.modal_type.clone();
    let show_randomize = state.show_randomize;
    let mut input_value = use_signal(|| state.default_value.clone());
    let mut randomizing = use_signal(|| false);

    // Sync input value whenever the modal opens with a new default_value,
    // since use_signal's initializer only runs on first mount.
    let default_value = state.default_value.clone();
    use_effect(move || {
        input_value.set(default_value.clone());
    });

    rsx! {
        div {
            class: "modal modal-open z-[100]",
            onkeydown: move |e: Event<KeyboardData>| {
                if e.key() == Key::Escape {
                    match MODAL_STATE.read().modal_type {
                        Some(ModalType::Alert) => resolve_modal(ModalResult::Dismissed),
                        Some(ModalType::Confirm) => resolve_modal(ModalResult::Confirmed(false)),
                        Some(ModalType::Prompt) => resolve_modal(ModalResult::Text(None)),
                        None => {}
                    }
                }
            },
            div { class: "modal-box max-w-sm",
                if !state.title.is_empty() {
                    h3 { class: "font-bold text-lg mb-2", "{state.title}" }
                }
                p { class: "py-2 whitespace-pre-line text-sm", "{state.message}" }

                // Prompt input
                if matches!(modal_type, Some(ModalType::Prompt)) {
                    input {
                        class: "input input-bordered input-sm w-full mt-2",
                        r#type: "text",
                        value: "{input_value}",
                        oninput: move |e| input_value.set(e.value()),
                    }
                }

                div { class: "modal-action",
                    match modal_type {
                        Some(ModalType::Alert) => rsx! {
                            button {
                                class: "btn btn-sm btn-primary",
                                onclick: move |_| resolve_modal(ModalResult::Dismissed),
                                "OK"
                            }
                        },
                        Some(ModalType::Confirm) => rsx! {
                            button {
                                class: "btn btn-sm btn-ghost",
                                onclick: move |_| resolve_modal(ModalResult::Confirmed(false)),
                                "Cancel"
                            }
                            button {
                                class: "btn btn-sm btn-primary",
                                onclick: move |_| resolve_modal(ModalResult::Confirmed(true)),
                                "OK"
                            }
                        },
                        Some(ModalType::Prompt) => rsx! {
                            button {
                                class: "btn btn-sm btn-ghost",
                                disabled: *randomizing.read(),
                                onclick: move |_| resolve_modal(ModalResult::Text(None)),
                                "Cancel"
                            }
                            if show_randomize {
                                button {
                                    class: "btn btn-sm btn-ghost",
                                    disabled: *randomizing.read(),
                                    onclick: move |_| {
                                        spawn(async move {
                                            if crate::state::cooldown::is_on_cooldown("randomize_name") {
                                                return;
                                            }
                                            crate::state::cooldown::set_cooldown("randomize_name", 3);
                                            randomizing.set(true);
                                            match crate::api::client::randomize_display_name().await {
                                                Ok(updated) => {
                                                    let new_name = updated.display_name.clone().unwrap_or_default();
                                                    input_value.set(new_name);
                                                    *crate::state::auth::USER.write() = Some(crate::app::convert_user_info(updated));
                                                }
                                                Err(e) => {
                                                    tracing::error!("Failed to randomize display name: {}", e);
                                                    crate::state::toast::add_toast("Failed to randomize name", crate::state::toast::ToastType::Error, None);
                                                }
                                            }
                                            randomizing.set(false);
                                        });
                                    },
                                    "Randomize"
                                }
                            }
                            button {
                                class: "btn btn-sm btn-primary",
                                disabled: *randomizing.read(),
                                onclick: move |_| {
                                    let val = input_value.read().clone();
                                    resolve_modal(ModalResult::Text(Some(val)));
                                },
                                "OK"
                            }
                        },
                        None => rsx! {},
                    }
                }
            }
            // Backdrop — dismiss on click for all modal types
            div {
                class: "modal-backdrop",
                onclick: move |_| {
                    match MODAL_STATE.read().modal_type {
                        Some(ModalType::Alert) => resolve_modal(ModalResult::Dismissed),
                        Some(ModalType::Confirm) => resolve_modal(ModalResult::Confirmed(false)),
                        Some(ModalType::Prompt) => resolve_modal(ModalResult::Text(None)),
                        None => {}
                    }
                },
            }
        }
    }
}
