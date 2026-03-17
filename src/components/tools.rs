use dioxus::prelude::*;
use tracing::error;

use crate::backend::autoseed::open_logs;
use crate::backend::backup::{
    backup_user_settings, has_auto_backup, has_backup, get_manual_backup_path,
    restore_from_auto_backup, restore_user_settings,
};
use crate::backend::session::{get_stored_session, store_session};
use crate::backend::tools::get_default_config_path;
use crate::state::modal::{show_alert, show_confirm};

const BASE_URL: &str = "https://hll-gw-seeder.netlify.app";

struct LinkInfo {
    label: &'static str,
    url: String,
    icon: &'static str,
}

fn get_links() -> Vec<LinkInfo> {
    vec![
        LinkInfo {
            label: "Home Page",
            url: BASE_URL.to_string(),
            icon: "\u{1F3E0}",
        },
        LinkInfo {
            label: "FAQ",
            url: format!("{}/faq", BASE_URL),
            icon: "\u{2753}",
        },
        LinkInfo {
            label: "Terms and Conditions",
            url: format!("{}/termsandconditions", BASE_URL),
            icon: "\u{1F4DC}",
        },
        LinkInfo {
            label: "Privacy Policy",
            url: format!("{}/privacypolicy", BASE_URL),
            icon: "\u{1F512}",
        },
    ]
}

/// Get the HLL config path from session storage or fall back to default.
fn get_config_path() -> String {
    if let Some(stored) = get_stored_session("hll_config_path") {
        if !stored.is_empty() {
            return stored;
        }
    }
    get_default_config_path().unwrap_or_default()
}

/// Persist the user-selected config path to session storage.
fn save_config_path(path: &str) {
    let _ = store_session("hll_config_path", path.to_string());
}

/// Pick a directory using rfd::FileDialog on a blocking thread.
async fn pick_folder(title: &str, default_path: &str) -> Option<String> {
    let title = title.to_string();
    let default_path = default_path.to_string();
    tokio::task::spawn_blocking(move || {
        let mut dialog = rfd::FileDialog::new().set_title(&title);
        if !default_path.is_empty() {
            dialog = dialog.set_directory(&default_path);
        }
        dialog.pick_folder().map(|p| p.to_string_lossy().to_string())
    })
    .await
    .ok()
    .flatten()
}

#[component]
pub fn Tools() -> Element {
    let mut show_links = use_signal(|| false);

    // Handler: open application logs
    let handle_open_logs = move |_| {
        spawn(async move {
            if let Err(e) = open_logs().await {
                error!("Error opening logs: {}", e);
                show_alert("Failed to open log file.", "Error").await;
            }
        });
    };

    // Handler: backup game settings
    let handle_backup_game_settings = move |_| {
        spawn(async move {
            let default_path = get_config_path();

            let selected = match pick_folder(
                "Select HLL Config folder (e.g. AppData/Local/HLL/Saved/Config/WindowsNoEditor)",
                &default_path,
            )
            .await
            {
                Some(path) => path,
                None => return,
            };

            save_config_path(&selected);

            match backup_user_settings(selected).await {
                Ok(count) => {
                    show_alert(
                        &format!("Backed up {} config file(s) to ~/espritseeder-backup/HLL/manual/", count),
                        "Backup Complete",
                    )
                    .await;
                }
                Err(e) => {
                    error!("Backup failed: {}", e);
                    show_alert(
                        "Failed to back up game settings. Check the logs for details.",
                        "Error",
                    )
                    .await;
                }
            }
        });
    };

    // Handler: restore from manual backup
    let handle_restore_game_settings = move |_| {
        spawn(async move {
            if !has_backup() {
                show_alert("No manual backup found. Please create a backup first.", "Info").await;
                return;
            }

            let manual_path = match get_manual_backup_path() {
                Ok(p) => p,
                Err(e) => {
                    error!("Restore failed: {}", e);
                    show_alert(
                        "Failed to restore game settings. Check the logs for details.",
                        "Error",
                    )
                    .await;
                    return;
                }
            };

            // Select backup folder to restore from
            let selected_backup = match pick_folder(
                "Select backup folder to restore from",
                &manual_path,
            )
            .await
            {
                Some(path) => path,
                None => return,
            };

            let default_path = get_config_path();

            // Select destination folder
            let selected_dest = match pick_folder(
                "Select HLL Config folder to restore to",
                &default_path,
            )
            .await
            {
                Some(path) => path,
                None => return,
            };

            save_config_path(&selected_dest);

            match restore_user_settings(selected_backup, selected_dest).await {
                Ok(count) if count > 0 => {
                    show_alert(
                        &format!("Restored {} config file(s) from backup.", count),
                        "Restore Complete",
                    )
                    .await;
                }
                Ok(_) => {
                    show_alert("No files were restored.", "Info").await;
                }
                Err(e) => {
                    error!("Restore failed: {}", e);
                    show_alert(
                        "Failed to restore game settings. Check the logs for details.",
                        "Error",
                    )
                    .await;
                }
            }
        });
    };

    // Handler: restore from automatic backup
    let handle_restore_auto_backup = move |_| {
        spawn(async move {
            if !has_auto_backup() {
                show_alert(
                    "No automatic backup found.\n\nAn automatic backup is created before each seeding session.",
                    "Info",
                )
                .await;
                return;
            }

            if !show_confirm(
                "Restore game settings from the last automatic backup?\n\n\
                 This was saved before your last seeding session started.",
                "Confirm Restore",
            )
            .await
            {
                return;
            }

            match restore_from_auto_backup() {
                Ok(msg) => {
                    show_alert(&msg, "Restore Complete").await;
                }
                Err(e) => {
                    error!("Auto-restore failed: {}", e);
                    show_alert(
                        "Failed to restore from automatic backup. Check the logs for details.",
                        "Error",
                    )
                    .await;
                }
            }
        });
    };

    if *show_links.read() {
        // Links Submenu
        rsx! {
            div { class: "flex flex-col w-full p-4",
                button {
                    class: "btn btn-ghost btn-sm gap-2 self-start mb-4",
                    onclick: move |_| show_links.set(false),
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
                            d: "M15 19l-7-7 7-7",
                        }
                    }
                    "Back to Tools"
                }
                h2 { class: "text-lg font-semibold mb-4 text-center", "Web Resources" }
                div { class: "flex flex-col gap-3 w-full",
                    for link in get_links() {
                        {
                            let url = link.url.clone();
                            rsx! {
                                button {
                                    class: "btn btn-outline btn-block justify-start gap-3",
                                    onclick: move |_| {
                                        let url = url.clone();
                                        let _ = open::that(&url);
                                    },
                                    span { class: "text-lg", "{link.icon}" }
                                    span { "{link.label}" }
                                }
                            }
                        }
                    }
                }
            }
        }
    } else {
        // Main Tools Menu
        rsx! {
            div { class: "flex flex-col w-full justify-center p-4",
                div { class: "w-full",
                    // Row 1: Backup / Restore Manual / Restore Auto
                    div { class: "flex",
                        button {
                            class: "btn btn-ghost flex-col gap-1 w-1/3 h-16 text-xs",
                            "aria-label": "Backup game settings",
                            onclick: handle_backup_game_settings,
                            img { src: asset!("/assets/setup_icon.png"), style: "width: 1.5rem; height: 1.5rem; object-fit: contain;", alt: "" }
                            "Backup Settings"
                        }
                        button {
                            class: "btn btn-ghost flex-col gap-1 w-1/3 h-16 text-xs",
                            "aria-label": "Restore from manual backup",
                            onclick: handle_restore_game_settings,
                            img { src: asset!("/assets/setup_icon.png"), style: "width: 1.5rem; height: 1.5rem; object-fit: contain;", alt: "" }
                            "Restore Manual"
                        }
                        button {
                            class: "btn btn-ghost flex-col gap-1 w-1/3 h-16 text-xs",
                            "aria-label": "Restore from automatic backup",
                            onclick: handle_restore_auto_backup,
                            img { src: asset!("/assets/setup_icon.png"), style: "width: 1.5rem; height: 1.5rem; object-fit: contain;", alt: "" }
                            "Restore Auto"
                        }
                    }
                    // Row 2: View Logs / Links & Resources
                    div { class: "flex",
                        button {
                            class: "btn btn-ghost flex-col gap-1 w-1/2 h-16 text-xs",
                            "aria-label": "Open application logs",
                            onclick: handle_open_logs,
                            img { src: asset!("/assets/logs.png"), style: "width: 1.5rem; height: 1.5rem; object-fit: contain;", alt: "" }
                            "View Logs"
                        }
                        button {
                            class: "btn btn-ghost flex-col gap-1 w-1/2 h-14 text-xs",
                            onclick: move |_| show_links.set(true),
                            svg {
                                xmlns: "http://www.w3.org/2000/svg",
                                class: "h-5 w-5",
                                fill: "none",
                                view_box: "0 0 24 24",
                                stroke: "currentColor",
                                path {
                                    stroke_linecap: "round",
                                    stroke_linejoin: "round",
                                    stroke_width: "2",
                                    d: "M13.828 10.172a4 4 0 00-5.656 0l-4 4a4 4 0 105.656 5.656l1.102-1.101m-.758-4.899a4 4 0 005.656 0l4-4a4 4 0 00-5.656-5.656l-1.1 1.1",
                                }
                            }
                            "Links & Resources"
                        }
                    }
                }
            }
        }
    }
}
