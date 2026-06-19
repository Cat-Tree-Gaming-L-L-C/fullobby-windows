use dioxus::prelude::*;

use crate::state::servers::{SSE_CONNECTED, SSE_FAILURE_COUNT};
use crate::state::stale_timer::STALE_INFO;

/// Number of consecutive SSE failures before we show "Polling" instead of "Disconnected".
const POLLING_FALLBACK_THRESHOLD: u32 = 3;

/// Reconnect button shown only when SSE is disconnected.
/// Shows "Disconnected" (yellow) for the first few failures,
/// then switches to a neutral "Polling" state once the polling
/// fallback is reliably delivering data.
#[component]
pub fn ReconnectButton() -> Element {
    let sse_connected = *SSE_CONNECTED.read();

    // Only render when SSE is disconnected
    if sse_connected {
        return rsx! {};
    }

    let failure_count = *SSE_FAILURE_COUNT.read();
    let stale_info = STALE_INFO.read();
    let seconds_ago = stale_info.seconds_ago;
    let polling = failure_count >= POLLING_FALLBACK_THRESHOLD;

    rsx! {
        div {
            class: if polling {
                "flex items-center justify-center gap-2 mt-1 text-xs text-base-content/60"
            } else {
                "flex items-center justify-center gap-2 mt-1 text-xs text-warning"
            },
            span { class: "flex items-center gap-1",
                span {
                    class: if polling {
                        "inline-block w-1.5 h-1.5 rounded-full bg-base-content/40"
                    } else {
                        "inline-block w-1.5 h-1.5 rounded-full bg-warning"
                    },
                }
                span { class: "font-medium",
                    if polling { "Polling" } else { "Disconnected" }
                }
            }
            span { class: "text-base-content/60", "Updated {seconds_ago}s ago" }
            button {
                class: "btn btn-outline btn-xs",
                onclick: move |_| {
                    crate::api::sse::request_reconnect();
                },
                "Reconnect"
            }
        }
    }
}
