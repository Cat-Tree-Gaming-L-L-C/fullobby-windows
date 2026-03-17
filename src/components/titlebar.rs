use dioxus::prelude::*;

use crate::state::stale_timer::STALE_INFO;

#[component]
pub fn Titlebar() -> Element {
    let stale_info = STALE_INFO.read();
    let is_offline = stale_info.stale;
    let seconds_ago = stale_info.seconds_ago;
    drop(stale_info);

    rsx! {
        div {
            id: "titlebar",
            class: "fixed top-0 left-0 right-0 h-8 flex items-center justify-between bg-base-300 z-50 select-none",
            // Make the titlebar draggable
            style: "-webkit-app-region: drag",

            div { class: "flex items-center gap-2 pl-3",
                span {
                    id: "title-version",
                    class: "text-xs font-medium opacity-70",
                    "Esprit Seeder v{env!(\"CARGO_PKG_VERSION\")}"
                }
                // Connectivity indicator
                if is_offline {
                    span {
                        class: "flex items-center gap-1 text-warning text-[10px] opacity-80",
                        title: "Last update {seconds_ago}s ago",
                        // Warning dot
                        span { class: "inline-block w-1.5 h-1.5 rounded-full bg-warning animate-pulse" }
                        "offline"
                    }
                }
            }

            div {
                class: "flex items-center",
                style: "-webkit-app-region: no-drag",
                button {
                    id: "titlebar-minimize",
                    class: "btn btn-ghost btn-xs h-8 w-10 rounded-none",
                    onclick: move |_| {
                        crate::platform::tray::minimize_main_window();
                    },
                    svg {
                        xmlns: "http://www.w3.org/2000/svg",
                        class: "h-4 w-4",
                        fill: "none",
                        view_box: "0 0 24 24",
                        stroke: "currentColor",
                        path {
                            stroke_linecap: "round",
                            stroke_linejoin: "round",
                            stroke_width: "2",
                            d: "M20 12H4"
                        }
                    }
                }
                button {
                    id: "titlebar-close",
                    class: "btn btn-ghost btn-xs h-8 w-10 rounded-none hover:bg-error hover:text-error-content",
                    onclick: move |_| {
                        let close_to_tray = crate::backend::session::get_stored_session("close_to_tray")
                            .map(|v| v == "true")
                            .unwrap_or(true);
                        if close_to_tray {
                            crate::platform::tray::hide_main_window();
                        } else {
                            crate::config::flush_pending_saves();
                            crate::backend::heartbeat::stop_heartbeat_sync(Some("app_exit".into()));
                            spawn(async move {
                                crate::backend::seeding::cleanup_efficiency_on_exit().await;
                                crate::platform::tray::close_app();
                            });
                        }
                    },
                    svg {
                        xmlns: "http://www.w3.org/2000/svg",
                        class: "h-4 w-4",
                        fill: "none",
                        view_box: "0 0 24 24",
                        stroke: "currentColor",
                        path {
                            stroke_linecap: "round",
                            stroke_linejoin: "round",
                            stroke_width: "2",
                            d: "M6 18L18 6M6 6l12 12"
                        }
                    }
                }
            }
        }
    }
}
