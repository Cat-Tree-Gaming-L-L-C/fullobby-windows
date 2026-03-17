use dioxus::prelude::*;
use once_cell::sync::Lazy;
use regex::Regex;
use tracing::error;

use crate::backend::autoseed;

/// Compiled regex for auto-seed time input validation (HH:MM format).
static AUTO_SEED_TIME_REGEX: Lazy<Regex> = Lazy::new(|| {
    Regex::new(r"^(\d{1,2}):(\d{2})$").expect("Invalid time regex")
});
use crate::backend::session::{get_stored_session, store_session};
use crate::state::auth::{AuthMethod, AUTH_LOADING, AUTH_METHOD, IS_GUEST, IS_LOGGED_IN, USER};
use crate::state::modal::{show_alert, show_confirm, show_prompt, show_prompt_with_randomize};
use crate::state::servers::EU_ENABLED;
use crate::state::session::{save_efficiency_mode, LINKED_STEAM_IDS, LINKED_PROVIDERS};
use crate::state::toast::{add_toast, ToastType};

/// Autoseed task status for display
#[derive(Clone, Default)]
struct AutoseedDisplayStatus {
    na_installed: bool,
    na_next_run: Option<String>,
    eu_installed: bool,
    eu_next_run: Option<String>,
}

/// Configuration for NA vs EU auto-seed setup
struct AutoSeedConfig {
    default_utc: &'static str,
    default_h: u32,
    default_m: u32,
    label: &'static str,
    store_key: &'static str,
    extra_prompt: &'static str,
}

const NA_CONFIG: AutoSeedConfig = AutoSeedConfig {
    default_utc: "12:00",
    default_h: 12,
    default_m: 0,
    label: "NA",
    store_key: "auto_seed_time",
    extra_prompt: "",
};

const EU_CONFIG: AutoSeedConfig = AutoSeedConfig {
    default_utc: "06:00",
    default_h: 6,
    default_m: 0,
    label: "EU",
    store_key: "auto_seed_time_secondary",
    extra_prompt: "\n\nThis creates a separate scheduled task that only seeds EU servers.",
};

/// Convert UTC hours/minutes to local time display string
fn utc_to_local_display(utc_hours: u32, utc_minutes: u32) -> String {
    use chrono::{NaiveTime, TimeZone, Utc};
    let now = chrono::Local::now();
    let today = now.date_naive();
    let utc_dt = Utc
        .from_utc_datetime(&today.and_time(NaiveTime::from_hms_opt(utc_hours, utc_minutes, 0).unwrap_or_default()));
    let local_dt = utc_dt.with_timezone(&chrono::Local);
    local_dt.format("%-I:%M %p").to_string()
}

/// Convert UTC hours/minutes to local hours/minutes for schtasks
fn utc_to_local(utc_hours: u32, utc_minutes: u32) -> (u32, u32) {
    use chrono::{NaiveTime, TimeZone, Utc};
    let now = chrono::Local::now();
    let today = now.date_naive();
    let utc_dt = Utc
        .from_utc_datetime(&today.and_time(NaiveTime::from_hms_opt(utc_hours, utc_minutes, 0).unwrap_or_default()));
    let local_dt = utc_dt.with_timezone(&chrono::Local);
    (local_dt.format("%H").to_string().parse().unwrap_or(0), local_dt.format("%M").to_string().parse().unwrap_or(0))
}

/// Get the local timezone name
fn get_timezone_name() -> String {
    let now = chrono::Local::now();
    now.format("%Z").to_string()
}


#[component]
pub fn Settings() -> Element {
    // Local state
    let mut eu_enabled = use_signal(|| false);
    let mut bypass_duration = use_signal(|| "20".to_string());
    let mut enabled_games: Signal<Vec<String>> = use_signal(|| vec!["hll".to_string()]);
    let mut dark_mode = use_signal(|| false);
    let mut efficiency_enabled = use_signal(|| false);
    let mut switch_notification_enabled = use_signal(|| false);
    let mut close_to_tray = use_signal(|| true);
    let mut beta_updates = use_signal(|| false);

    // Per-toggle loading flags
    let mut eu_toggle_loading = use_signal(|| false);
    let mut dark_mode_loading = use_signal(|| false);
    let mut efficiency_loading = use_signal(|| false);
    let mut switch_notif_loading = use_signal(|| false);
    let mut close_to_tray_loading = use_signal(|| false);
    let mut start_with_windows = use_signal(|| false);
    let mut start_with_windows_loading = use_signal(|| false);

    // Auto-seed setup loading state
    let na_setup_loading = use_signal(|| false);
    let eu_setup_loading = use_signal(|| false);

    // Auto-seed task status
    let mut autoseed_status = use_signal(AutoseedDisplayStatus::default);

    // Refresh autoseed status helper
    let refresh_autoseed = move || {
        spawn(async move {
            let status = autoseed::get_autoseed_status().await;
            autoseed_status.set(AutoseedDisplayStatus {
                na_installed: status.na_installed,
                na_next_run: status.na_next_run,
                eu_installed: status.eu_installed,
                eu_next_run: status.eu_next_run,
            });
        });
    };

    // Initialize settings on mount
    {
        let refresh_autoseed = refresh_autoseed.clone();
        use_effect(move || {
            // Load settings from session storage
            let eu = get_stored_session("secondary_servers_enabled")
                .map(|v| v == "true")
                .unwrap_or(false);
            let efficiency = get_stored_session("efficiency_mode")
                .map(|v| v == "true")
                .unwrap_or(false);
            let switch_notif = get_stored_session("switch_notification")
                .map(|v| v == "true")
                .unwrap_or(false);
            let stored_duration = get_stored_session("splash_bypass_duration");
            let stored_theme = get_stored_session("theme");

            eu_enabled.set(eu);
            efficiency_enabled.set(efficiency);
            switch_notification_enabled.set(switch_notif);
            if let Some(d) = stored_duration {
                bypass_duration.set(d);
            }
            dark_mode.set(stored_theme.as_deref() == Some("dark"));

            let close_tray = get_stored_session("close_to_tray")
                .map(|v| v == "true")
                .unwrap_or(true);
            close_to_tray.set(close_tray);

            start_with_windows.set(crate::platform::startup::is_startup_enabled());
            beta_updates.set(
                get_stored_session("update_channel")
                    .map(|v| v == "beta")
                    .unwrap_or(false),
            );

            // Load enabled games from config
            let games = crate::config::get("enabled_games")
                .and_then(|v| serde_json::from_value::<Vec<String>>(v).ok())
                .unwrap_or_else(|| vec!["hll".to_string()]);
            enabled_games.set(games);

            refresh_autoseed();
        });
    }

    rsx! {
        div {
            class: "flex flex-col w-full p-6 pt-6 overflow-y-auto max-h-[calc(100vh-96px)]",
            div { class: "w-full",
                // Discord Account Section
                div { class: "divider my-2 text-xs", "Account" }

                {render_auth_section()}

                // Player section
                div { class: "divider my-2 text-xs", "Player" }

                div { class: "flex",
                    button {
                        class: if *IS_LOGGED_IN.read() {
                            "btn btn-ghost flex-col gap-1 w-1/2 h-14 text-xs"
                        } else {
                            "btn btn-ghost flex-col gap-1 w-1/2 h-14 text-xs btn-disabled opacity-40"
                        },
                        disabled: !*IS_LOGGED_IN.read(),
                        aria_label: "Change display name",
                        title: if !*IS_LOGGED_IN.read() { "Log in to change your name" } else { "" },
                        onclick: move |_| {
                            spawn(async move {
                                // Guest (API key) users can only randomize
                                if *IS_GUEST.read() {
                                    if crate::state::cooldown::is_on_cooldown("randomize_name") {
                                        return;
                                    }
                                    crate::state::cooldown::set_cooldown("randomize_name", 3);
                                    match crate::api::client::randomize_display_name().await {
                                        Ok(updated) => {
                                            *USER.write() = Some(crate::app::convert_user_info(updated));
                                            add_toast("Name randomized", ToastType::Success, None);
                                        }
                                        Err(e) => {
                                            error!("Failed to randomize display name: {}", e);
                                            let msg = crate::api::client::friendly_error(e.as_ref());
                                            add_toast(&msg, ToastType::Error, None);
                                        }
                                    }
                                    return;
                                }

                                // Pre-fill with current display_name or username
                                let current = USER.read()
                                    .as_ref()
                                    .and_then(|u| u.display_name.clone().or(Some(u.username.clone())))
                                    .unwrap_or_default();

                                let result = show_prompt_with_randomize(
                                    "Enter a new display name (2-32 chars).",
                                    &current,
                                    "Change Name",
                                ).await;

                                if let Some(new_name) = result {
                                    let trimmed = new_name.trim().to_string();
                                    if trimmed.is_empty() {
                                        return;
                                    }
                                    // Client-side validation before API call
                                    if let Err(msg) = crate::api::client::validate_display_name(&trimmed) {
                                        add_toast(&msg, ToastType::Error, None);
                                        return;
                                    }
                                    if crate::state::cooldown::is_on_cooldown("update_name") {
                                        return;
                                    }
                                    crate::state::cooldown::set_cooldown("update_name", 3);
                                    match crate::api::client::update_display_name(&trimmed).await {
                                        Ok(updated) => {
                                            *USER.write() = Some(crate::app::convert_user_info(updated));
                                            add_toast("Display name updated", ToastType::Success, None);
                                        }
                                        Err(e) => {
                                            error!("Failed to update display name: {}", e);
                                            let msg = crate::api::client::friendly_error(e.as_ref());
                                            add_toast(&msg, ToastType::Error, None);
                                        }
                                    }
                                }
                            });
                        },
                        img { src: asset!("/assets/person.svg"), style: "width: 1.25rem; height: 1.25rem; object-fit: contain;", alt: "" }
                        if *IS_GUEST.read() { "Randomize Name" } else { "Change Name" }
                    }
                    button {
                        class: "btn btn-ghost flex-col gap-1 w-1/2 h-14 text-xs",
                        aria_label: "Check for app updates",
                        onclick: move |_| {
                            spawn(async move {
                                match crate::platform::updater::check_for_updates().await {
                                    Ok(Some(info)) => {
                                        show_alert(
                                            &format!(
                                                "Update available: v{}\n\n{}\n\nDownload: {}",
                                                info.version, info.notes, info.download_url
                                            ),
                                            "Update Available",
                                        ).await;
                                    }
                                    Ok(None) => {
                                        show_alert(
                                            "You are running the latest version.",
                                            "No Updates",
                                        ).await;
                                    }
                                    Err(e) => {
                                        error!("Update check failed: {}", e);
                                        show_alert("Failed to check for updates.", "Error").await;
                                    }
                                }
                            });
                        },
                        img { src: asset!("/assets/update.png"), style: "width: 1.25rem; height: 1.25rem; object-fit: contain;", alt: "" }
                        "Check Updates"
                    }
                }

                // Leaderboard opt-out toggle
                if *IS_LOGGED_IN.read() {
                    div { class: "flex items-center justify-between w-full px-4 py-2",
                        div { class: "flex flex-col",
                            span { class: "text-sm", "Show on Leaderboard" }
                            span { class: "text-xs opacity-50", "Appear in public rankings" }
                        }
                        input {
                            r#type: "checkbox",
                            class: "toggle toggle-primary toggle-sm",
                            checked: !USER.read().as_ref().map(|u| u.leaderboard_opt_out).unwrap_or(false),
                            onclick: move |_| {
                                spawn(async move {
                                    let current_opt_out = USER.read()
                                        .as_ref()
                                        .map(|u| u.leaderboard_opt_out)
                                        .unwrap_or(false);
                                    let new_opt_out = !current_opt_out;
                                    match crate::api::client::update_leaderboard_opt_out(new_opt_out).await {
                                        Ok(updated) => {
                                            *USER.write() = Some(crate::app::convert_user_info(updated));
                                            let msg = if new_opt_out {
                                                "Removed from leaderboard"
                                            } else {
                                                "Now visible on leaderboard"
                                            };
                                            add_toast(msg, ToastType::Success, None);
                                        }
                                        Err(e) => {
                                            error!("Failed to update leaderboard preference: {}", e);
                                            let msg = crate::api::client::friendly_error(e.as_ref());
                                            add_toast(&msg, ToastType::Error, None);
                                        }
                                    }
                                });
                            },
                        }
                    }
                }

                div { class: "divider my-2 text-xs", "Settings" }

                // Enable EU Servers toggle
                div { class: "flex items-center justify-between w-full px-4 py-2",
                    span { class: "text-sm", "Enable EU Servers" }
                    div { class: "flex items-center gap-2",
                        if *eu_toggle_loading.read() {
                            span { class: "loading loading-spinner loading-xs" }
                        }
                        input {
                            r#type: "checkbox",
                            class: "toggle toggle-primary toggle-sm",
                            checked: *eu_enabled.read(),
                            disabled: *eu_toggle_loading.read(),
                            onchange: {
                                let refresh_autoseed = refresh_autoseed.clone();
                                move |_| {
                                    let refresh_autoseed = refresh_autoseed.clone();
                                    spawn(async move {
                                        eu_toggle_loading.set(true);
                                        let current = *eu_enabled.read();
                                        if current {
                                            // Trying to disable -- check if EU autoseed task is installed
                                            let installed = autoseed::is_eu_autoseed_installed().await;
                                            if installed {
                                                show_alert(
                                                    "Please uninstall the EU auto-seed scheduled task before disabling EU seeding.",
                                                    "EU Seeding",
                                                ).await;
                                                eu_toggle_loading.set(false);
                                                return;
                                            }
                                        }
                                        let new_val = !current;
                                        eu_enabled.set(new_val);
                                        let val_str = if new_val { "true" } else { "false" };
                                        if let Err(e) = store_session("secondary_servers_enabled", val_str.to_string()) {
                                            error!("Failed to toggle EU servers: {}", e);
                                            eu_enabled.set(current);
                                            add_toast("Failed to save EU servers setting", ToastType::Error, None);
                                        } else {
                                            *EU_ENABLED.write() = new_val;
                                        }
                                        eu_toggle_loading.set(false);
                                        let _ = refresh_autoseed;
                                    });
                                }
                            },
                        }
                    }
                }

                // Games section (only shown when multiple games are released)
                if crate::backend::game::RELEASED_GAMES.len() > 1 {
                    div { class: "divider my-2 text-xs", "Games" }
                    p { class: "text-xs text-base-content/60 px-4 mb-1",
                        "Select which games to seed. Seed All will try all enabled games."
                    }
                    {
                        let games_list = enabled_games.read().clone();
                        rsx! {
                            for game in crate::backend::game::RELEASED_GAMES.iter() {
                                {
                                    let game_id = game.id.to_string();
                                    let display = game.display_name;
                                    let is_checked = games_list.contains(&game_id);
                                    rsx! {
                                        div { class: "flex items-center justify-between w-full px-4 py-2",
                                            span { class: "text-sm", "{display}" }
                                            input {
                                                r#type: "checkbox",
                                                class: "toggle toggle-primary toggle-sm",
                                                checked: is_checked,
                                                onchange: {
                                                    let game_id = game_id.clone();
                                                    move |_| {
                                                        let game_id = game_id.clone();
                                                        let mut current = enabled_games.read().clone();
                                                        if current.contains(&game_id) {
                                                            // Don't allow disabling the last game
                                                            if current.len() <= 1 {
                                                                add_toast("At least one game must be enabled", ToastType::Info, None);
                                                                return;
                                                            }
                                                            current.retain(|g| g != &game_id);
                                                        } else {
                                                            current.push(game_id);
                                                        }
                                                        if let Err(e) = crate::config::set("enabled_games", &current) {
                                                            error!("Failed to save enabled games: {}", e);
                                                            add_toast("Failed to save games setting", ToastType::Error, None);
                                                        } else {
                                                            enabled_games.set(current);
                                                        }
                                                    }
                                                },
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                div { class: "divider my-2 text-xs", "" }

                // Dark Mode toggle
                div { class: "flex items-center justify-between w-full px-4 py-2",
                    span { class: "text-sm", "Dark Mode" }
                    div { class: "flex items-center gap-2",
                        if *dark_mode_loading.read() {
                            span { class: "loading loading-spinner loading-xs" }
                        }
                        input {
                            r#type: "checkbox",
                            class: "toggle toggle-primary toggle-sm",
                            checked: *dark_mode.read(),
                            disabled: *dark_mode_loading.read(),
                            onchange: move |_| {
                                spawn(async move {
                                    dark_mode_loading.set(true);
                                    let new_val = !*dark_mode.read();
                                    dark_mode.set(new_val);
                                    let theme = if new_val { "dark" } else { "light" };
                                    let _ = document::eval(&format!(
                                        "document.documentElement.setAttribute('data-theme', '{}')",
                                        theme
                                    ));
                                    if let Err(e) = store_session("theme", theme.to_string()) {
                                        error!("Failed to save theme: {}", e);
                                        add_toast("Failed to save theme", ToastType::Error, None);
                                    }
                                    dark_mode_loading.set(false);
                                });
                            },
                        }
                    }
                }

                // Minimize to Tray on Close toggle
                div { class: "flex items-center justify-between w-full px-4 py-2",
                    span {
                        class: "text-sm",
                        title: "When enabled, closing the window minimizes to tray instead of exiting",
                        "Minimize to Tray on Close"
                    }
                    div { class: "flex items-center gap-2",
                        if *close_to_tray_loading.read() {
                            span { class: "loading loading-spinner loading-xs" }
                        }
                        input {
                            r#type: "checkbox",
                            class: "toggle toggle-primary toggle-sm",
                            checked: *close_to_tray.read(),
                            disabled: *close_to_tray_loading.read(),
                            onchange: move |_| {
                                spawn(async move {
                                    close_to_tray_loading.set(true);
                                    let new_val = !*close_to_tray.read();
                                    close_to_tray.set(new_val);
                                    let val_str = if new_val { "true" } else { "false" };
                                    if let Err(e) = store_session("close_to_tray", val_str.to_string()) {
                                        error!("Failed to save close to tray setting: {}", e);
                                        add_toast("Failed to save close to tray setting", ToastType::Error, None);
                                    }
                                    close_to_tray_loading.set(false);
                                });
                            },
                        }
                    }
                }

                // Start with Windows toggle
                div { class: "flex items-center justify-between w-full px-4 py-2",
                    span {
                        class: "text-sm",
                        title: "Launch Esprit Seeder automatically when you log in to Windows",
                        "Start with Windows"
                    }
                    div { class: "flex items-center gap-2",
                        if *start_with_windows_loading.read() {
                            span { class: "loading loading-spinner loading-xs" }
                        }
                        input {
                            r#type: "checkbox",
                            class: "toggle toggle-primary toggle-sm",
                            checked: *start_with_windows.read(),
                            disabled: *start_with_windows_loading.read(),
                            onchange: move |_| {
                                spawn(async move {
                                    start_with_windows_loading.set(true);
                                    let new_val = !*start_with_windows.read();
                                    let result = if new_val {
                                        crate::platform::startup::enable_startup()
                                    } else {
                                        crate::platform::startup::disable_startup()
                                    };
                                    match result {
                                        Ok(()) => {
                                            start_with_windows.set(new_val);
                                        }
                                        Err(e) => {
                                            error!("Failed to toggle startup: {}", e);
                                            add_toast("Failed to update startup setting", ToastType::Error, None);
                                        }
                                    }
                                    start_with_windows_loading.set(false);
                                });
                            },
                        }
                    }
                }

                // Seeding Power Savings toggle
                div { class: "flex items-center justify-between w-full px-4 py-2",
                    span {
                        class: "text-sm",
                        title: "Applies low-resource game settings and reduces server polling while seeding",
                        "Seeding Power Savings"
                    }
                    div { class: "flex items-center gap-2",
                        if *efficiency_loading.read() {
                            span { class: "loading loading-spinner loading-xs" }
                        }
                        input {
                            r#type: "checkbox",
                            class: "toggle toggle-primary toggle-sm",
                            checked: *efficiency_enabled.read(),
                            disabled: *efficiency_loading.read(),
                            onchange: move |_| {
                                spawn(async move {
                                    if !*efficiency_enabled.read() {
                                        // Show warning before enabling
                                        let confirmed = crate::state::modal::show_confirm(
                                            "Power Savings will temporarily edit your game's \
                                             GameUserSettings.ini to apply low-resource settings \
                                             while seeding. Your original settings are automatically \
                                             backed up and restored when seeding ends.\n\n\
                                             We recommend using the Backup tool (Tools tab) to \
                                             create a manual backup before enabling this feature.\n\n\
                                             Enable Power Savings?",
                                            "Power Savings",
                                        ).await;
                                        if !confirmed {
                                            return;
                                        }
                                    }
                                    efficiency_loading.set(true);
                                    let new_val = !*efficiency_enabled.read();
                                    efficiency_enabled.set(new_val);
                                    save_efficiency_mode(new_val);
                                    efficiency_loading.set(false);
                                });
                            },
                        }
                    }
                }

                // Desktop Notification on Switch toggle
                div { class: "flex items-center justify-between w-full px-4 py-2",
                    span {
                        class: "text-sm",
                        title: "Show a desktop notification before switching servers",
                        "Desktop Notification on Switch"
                    }
                    div { class: "flex items-center gap-2",
                        if *switch_notif_loading.read() {
                            span { class: "loading loading-spinner loading-xs" }
                        }
                        input {
                            r#type: "checkbox",
                            class: "toggle toggle-primary toggle-sm",
                            checked: *switch_notification_enabled.read(),
                            disabled: *switch_notif_loading.read(),
                            onchange: move |_| {
                                spawn(async move {
                                    switch_notif_loading.set(true);
                                    let new_val = !*switch_notification_enabled.read();
                                    switch_notification_enabled.set(new_val);
                                    let val_str = if new_val { "true" } else { "false" };
                                    if let Err(e) = store_session("switch_notification", val_str.to_string()) {
                                        error!("Failed to save notification setting: {}", e);
                                        add_toast("Failed to save notification setting", ToastType::Error, None);
                                    }
                                    switch_notif_loading.set(false);
                                });
                            },
                        }
                    }
                }

                // Splash Bypass Duration select
                div { class: "flex items-center justify-between w-full px-4 py-2",
                    span {
                        class: "text-sm",
                        title: "How long to send key presses to skip the intro video after game launch",
                        "Splash Bypass Duration"
                    }
                    select {
                        class: "select select-bordered select-xs w-24",
                        value: "{bypass_duration}",
                        onchange: move |e: Event<FormData>| {
                            let val = e.value();
                            bypass_duration.set(val.clone());
                            if let Err(err) = store_session("splash_bypass_duration", val) {
                                error!("Failed to save bypass duration: {}", err);
                                add_toast("Failed to save bypass duration", ToastType::Error, None);
                            }
                        },
                        option { value: "10", "10 sec" }
                        option { value: "20", "20 sec" }
                        option { value: "40", "40 sec" }
                        option { value: "60", "60 sec" }
                    }
                }

                // Beta Updates toggle
                div { class: "flex items-center justify-between w-full px-4 py-2",
                    div { class: "flex flex-col",
                        span { class: "text-sm", "Beta Updates" }
                        span { class: "text-xs opacity-50", "Receive pre-release versions" }
                    }
                    input {
                        r#type: "checkbox",
                        class: "toggle toggle-primary toggle-sm",
                        checked: *beta_updates.read(),
                        onchange: move |_| {
                            let new_val = !*beta_updates.read();
                            beta_updates.set(new_val);
                            let channel = if new_val { "beta" } else { "stable" };
                            if let Err(err) = store_session("update_channel", channel.to_string()) {
                                error!("Failed to save update channel: {}", err);
                                add_toast("Failed to save update channel", ToastType::Error, None);
                            } else {
                                let msg = if new_val { "Switched to beta update channel" } else { "Switched to stable update channel" };
                                add_toast(msg, ToastType::Success, None);
                            }
                        },
                    }
                }

                // Auto-Seed NA section
                {render_autoseed_section(
                    "NA",
                    autoseed_status.read().na_installed,
                    autoseed_status.read().na_next_run.clone(),
                    na_setup_loading,
                    false,
                    refresh_autoseed.clone(),
                )}

                // Auto-Seed EU section (only if EU enabled)
                if *eu_enabled.read() {
                    {render_autoseed_section(
                        "EU",
                        autoseed_status.read().eu_installed,
                        autoseed_status.read().eu_next_run.clone(),
                        eu_setup_loading,
                        true,
                        refresh_autoseed.clone(),
                    )}
                }

            }
        }
    }
}

/// Render the auth/account section based on auth state
fn render_auth_section() -> Element {
    let auth_loading = *AUTH_LOADING.read();
    let is_logged_in = *IS_LOGGED_IN.read();

    if auth_loading {
        return rsx! {
            div { class: "flex items-center justify-center w-full px-4 py-3",
                span { class: "loading loading-spinner loading-sm" }
            }
        };
    }

    if is_logged_in {
        return rsx! { AuthAccountSection {} };
    }

    // Not logged in — show all sign-in options
    rsx! {
        SettingsSignIn {}
    }
}

/// Logged-in account section with provider linking
#[component]
fn AuthAccountSection() -> Element {
    let user = USER.read();
    let linked_ids = LINKED_STEAM_IDS.read();
    let providers = LINKED_PROVIDERS.read();
    let mut link_loading = use_signal(|| None::<String>);
    let mut unlink_loading = use_signal(|| None::<String>);

    let Some(ref u) = *user else {
        return rsx! {};
    };

    let has_discord = u.discord_id.is_some();
    let username = u.display_name.clone().unwrap_or_else(|| u.username.clone());
    let linked_steam_ids = linked_ids.clone();
    let provider_count = providers.len();

    // Determine which providers are missing
    let has_discord_provider = providers.iter().any(|p| matches!(p.provider, crate::api::types::AuthProvider::Discord));

    rsx! {
        div { class: "flex items-center justify-between w-full px-4 py-2",
            div { class: "flex items-center gap-3",
                div { class: "flex flex-col",
                    span { class: "text-sm font-medium", "{username}" }
                    if !has_discord {
                        span { class: "text-xs opacity-50", "API Key auth" }
                    }
                }
            }
            div { class: "flex gap-1",
                if *crate::state::auth::IS_GUEST.read() {
                    button {
                        class: "btn btn-ghost btn-xs",
                        onclick: move |_| {
                            spawn(async move {
                                if crate::state::cooldown::is_on_cooldown("rotate_api_key") {
                                    return;
                                }
                                crate::state::cooldown::set_cooldown("rotate_api_key", 5);
                                match crate::api::client::rotate_api_key().await {
                                    Ok(new_key) => {
                                        let _ = store_session("api_key", new_key);
                                        add_toast("API key rotated", ToastType::Success, None);
                                    }
                                    Err(e) => {
                                        error!("Failed to rotate API key: {}", e);
                                        add_toast("Failed to rotate API key", ToastType::Error, None);
                                    }
                                }
                            });
                        },
                        "Rotate API Key"
                    }
                }
                button {
                    class: "btn btn-ghost btn-xs",
                    onclick: move |_| {
                        crate::api::client::clear_tokens();
                        crate::api::client::clear_api_key();
                        *USER.write() = None;
                        *IS_LOGGED_IN.write() = false;
                        *AUTH_METHOD.write() = AuthMethod::None;
                        *LINKED_PROVIDERS.write() = vec![];
                        let _ = store_session("auth_token", String::new());
                        let _ = store_session("auth_refresh_token", String::new());
                        let _ = store_session("api_key", String::new());
                        tracing::info!("Logged out");
                    },
                    "Sign Out"
                }
                button {
                    class: "btn btn-ghost btn-xs text-error",
                    onclick: move |_| {
                        spawn(async move {
                            let confirmed = show_confirm(
                                "This will permanently delete your account and all seeding history. This cannot be undone.",
                                "Delete Account",
                            ).await;
                            if !confirmed {
                                return;
                            }
                            if crate::state::cooldown::is_on_cooldown("delete_account") {
                                return;
                            }
                            crate::state::cooldown::set_cooldown("delete_account", 10);
                            match crate::api::client::delete_account().await {
                                Ok(_) => {
                                    // Clear all auth state
                                    crate::api::client::clear_tokens();
                                    crate::api::client::clear_api_key();
                                    *USER.write() = None;
                                    *IS_LOGGED_IN.write() = false;
                                    *AUTH_METHOD.write() = AuthMethod::None;
                                    *LINKED_PROVIDERS.write() = vec![];
                                    let _ = store_session("auth_token", String::new());
                                    let _ = store_session("auth_refresh_token", String::new());
                                    let _ = store_session("api_key", String::new());
                                    // Reset onboarding
                                    *crate::state::auth::ONBOARDING_COMPLETE.write() = false;
                                    *crate::state::auth::ONBOARDING_STEP.write() = 0;
                                    *crate::state::auth::IS_GUEST.write() = false;
                                    let _ = store_session("onboarding_complete", String::new());
                                    let _ = store_session("guest_mode", String::new());
                                    add_toast("Account deleted", ToastType::Success, None);
                                    tracing::info!("Account deleted");
                                }
                                Err(e) => {
                                    error!("Failed to delete account: {}", e);
                                    add_toast("Failed to delete account", ToastType::Error, None);
                                }
                            }
                        });
                    },
                    "Delete Account"
                }
            }
        }

        // Linked providers section
        if !providers.is_empty() {
            div { class: "px-4 py-1",
                p { class: "text-xs opacity-60 mb-1", "Linked Providers:" }
                for p in providers.iter() {
                    div { class: "flex items-center justify-between text-xs py-0.5",
                        span { class: "font-medium",
                            {match p.provider {
                                crate::api::types::AuthProvider::Steam => "Steam",
                                crate::api::types::AuthProvider::Discord => "Discord",
                                crate::api::types::AuthProvider::Guest => "Guest",
                            }}
                            if let Some(ref name) = p.display_name {
                                span { class: "opacity-60 ml-1", "({name})" }
                            }
                        }
                        if provider_count > 1 {
                            button {
                                class: "btn btn-ghost btn-xs text-error",
                                disabled: unlink_loading.read().is_some(),
                                onclick: {
                                    let provider_str = p.provider.to_string();
                                    move |_| {
                                        let provider_str = provider_str.clone();
                                        spawn(async move {
                                            if crate::state::cooldown::is_on_cooldown("unlink_provider") {
                                                return;
                                            }
                                            crate::state::cooldown::set_cooldown("unlink_provider", 3);
                                            unlink_loading.set(Some(provider_str.clone()));
                                            match crate::api::client::unlink_provider(&provider_str).await {
                                                Ok(_) => {
                                                    crate::state::session::refresh_linked_providers().await;
                                                    // Refresh user info too since unlinking may change user fields
                                                    if let Ok(me) = crate::api::client::get_me().await {
                                                        *USER.write() = Some(crate::app::convert_user_info(me));
                                                    }
                                                    add_toast("Provider unlinked", ToastType::Success, None);
                                                }
                                                Err(e) => {
                                                    error!("Failed to unlink provider: {}", e);
                                                    add_toast("Failed to unlink provider", ToastType::Error, None);
                                                }
                                            }
                                            unlink_loading.set(None);
                                        });
                                    }
                                },
                                if unlink_loading.read().as_deref() == Some(&p.provider.to_string()) {
                                    span { class: "loading loading-spinner loading-xs" }
                                } else {
                                    "Unlink"
                                }
                            }
                        }
                    }
                }
            }
        }

        // Link providers — always allow adding more Steam accounts, only show Discord if not linked
        div { class: "flex items-center justify-center w-full px-4 py-1 gap-2",
            button {
                class: "btn btn-ghost btn-xs gap-1",
                disabled: link_loading.read().is_some(),
                onclick: move |_| {
                    spawn(async move {
                        link_loading.set(Some("steam".to_string()));
                        match crate::api::client::get_link_redirect_url("steam").await {
                            Ok(resp) => {
                                if let Err(e) = open::that(&resp.redirect_url) {
                                    error!("Failed to open browser for Steam linking: {}", e);
                                    add_toast("Failed to open browser", ToastType::Error, None);
                                }
                            }
                            Err(e) => {
                                error!("Failed to get Steam link URL: {}", e);
                                add_toast("Failed to start Steam linking", ToastType::Error, None);
                            }
                        }
                        link_loading.set(None);
                    });
                },
                if link_loading.read().as_deref() == Some("steam") {
                    span { class: "loading loading-spinner loading-xs" }
                }
                "Link Steam"
            }
            if !has_discord_provider {
                button {
                    class: "btn btn-ghost btn-xs gap-1",
                    disabled: link_loading.read().is_some(),
                    onclick: move |_| {
                        spawn(async move {
                            link_loading.set(Some("discord".to_string()));
                            match crate::api::client::get_link_redirect_url("discord").await {
                                Ok(resp) => {
                                    if let Err(e) = open::that(&resp.redirect_url) {
                                        error!("Failed to open browser for Discord linking: {}", e);
                                        add_toast("Failed to open browser", ToastType::Error, None);
                                    }
                                }
                                Err(e) => {
                                    error!("Failed to get Discord link URL: {}", e);
                                    add_toast("Failed to start Discord linking", ToastType::Error, None);
                                }
                            }
                            link_loading.set(None);
                        });
                    },
                    if link_loading.read().as_deref() == Some("discord") {
                        span { class: "loading loading-spinner loading-xs" }
                    }
                    "Link Discord"
                }
            }
        }

        // Linked Steam accounts
        if !linked_steam_ids.is_empty() {
            div { class: "px-4 py-1",
                p { class: "text-xs opacity-60 mb-1", "Linked Steam IDs:" }
                for steam_id in linked_steam_ids.iter() {
                    div { class: "flex items-center justify-between text-xs py-0.5",
                        span { class: "font-mono opacity-80", "{steam_id}" }
                        button {
                            class: "btn btn-ghost btn-xs text-error",
                            onclick: {
                                let sid = steam_id.clone();
                                move |_| {
                                    let sid = sid.clone();
                                    spawn(async move {
                                        if crate::state::cooldown::is_on_cooldown("remove_steam") {
                                            return;
                                        }
                                        crate::state::cooldown::set_cooldown("remove_steam", 3);
                                        match crate::api::client::remove_steam_id(&sid).await {
                                            Ok(_) => {
                                                crate::state::session::refresh_steam_ids_from_api().await;
                                                add_toast("Steam ID removed", ToastType::Success, None);
                                            }
                                            Err(e) => {
                                                error!("Failed to remove Steam ID: {}", e);
                                                add_toast("Failed to remove Steam ID", ToastType::Error, None);
                                            }
                                        }
                                    });
                                }
                            },
                            "Remove"
                        }
                    }
                }
            }
        }

    }
}

/// Compact sign-in widget for the settings page (not logged in / guest)
#[component]
fn SettingsSignIn() -> Element {
    let mut loading_provider = use_signal(|| None::<String>);

    rsx! {
        div { class: "flex flex-col gap-2 w-full px-4 py-2",
            div { class: "flex gap-2",
                button {
                    class: "btn btn-primary btn-sm flex-1",
                    disabled: loading_provider.read().is_some(),
                    onclick: move |_| {
                        loading_provider.set(Some("steam".to_string()));
                        match crate::api::client::get_oauth_url("steam", &generate_uuid()) {
                            Ok(url) => {
                                if let Err(e) = open::that(&url) {
                                    error!("Failed to open browser for Steam login: {}", e);
                                    add_toast("Failed to open browser", ToastType::Error, None);
                                }
                            }
                            Err(e) => {
                                error!("Invalid OAuth provider: {}", e);
                            }
                        }
                        loading_provider.set(None);
                    },
                    if loading_provider.read().as_deref() == Some("steam") {
                        span { class: "loading loading-spinner loading-xs" }
                    }
                    "Steam"
                }
                button {
                    class: "btn btn-sm flex-1",
                    disabled: loading_provider.read().is_some(),
                    onclick: move |_| {
                        loading_provider.set(Some("discord".to_string()));
                        match crate::api::client::get_oauth_url("discord", &generate_uuid()) {
                            Ok(url) => {
                                if let Err(e) = open::that(&url) {
                                    error!("Failed to open browser for Discord login: {}", e);
                                    add_toast("Failed to open browser", ToastType::Error, None);
                                }
                            }
                            Err(e) => {
                                error!("Invalid OAuth provider: {}", e);
                            }
                        }
                        loading_provider.set(None);
                    },
                    if loading_provider.read().as_deref() == Some("discord") {
                        span { class: "loading loading-spinner loading-xs" }
                    }
                    "Discord"
                }
            }
        }
    }
}

/// Render an auto-seed section (NA or EU)
fn render_autoseed_section(
    label: &'static str,
    installed: bool,
    next_run: Option<String>,
    setup_loading: Signal<bool>,
    is_eu: bool,
    refresh_autoseed: impl Fn() + Clone + 'static,
) -> Element {
    let icon_style = if is_eu {
        "width: 1rem; height: 1rem; object-fit: contain;"
    } else {
        "width: 1.25rem; height: 1.25rem; object-fit: contain;"
    };
    let divider_label = format!("Auto-Seed {}", label);

    rsx! {
        div { class: "divider my-2 text-xs",
            "{divider_label}"
            if installed {
                span { class: "badge badge-success badge-xs ml-1", "Installed" }
            } else {
                span { class: "badge badge-ghost badge-xs ml-1", "Not installed" }
            }
        }

        if installed {
            if let Some(ref nr) = next_run {
                div { class: "text-xs text-center opacity-60 -mt-1 mb-1",
                    "Next run: {nr}"
                }
            }
        }

        div { class: "flex",
            // Setup button
            button {
                class: "btn btn-ghost flex-col gap-1 w-1/3 h-14 text-xs",
                aria_label: "Setup {label} auto-seed",
                disabled: *setup_loading.read(),
                onclick: {
                    let is_eu = is_eu;
                    let refresh_autoseed = refresh_autoseed.clone();
                    move |_| {
                        let refresh_autoseed = refresh_autoseed.clone();
                        spawn(async move {
                            setup_auto_seed_flow(is_eu, setup_loading).await;
                            refresh_autoseed();
                        });
                    }
                },
                if *setup_loading.read() {
                    span { class: "loading loading-spinner loading-xs" }
                } else {
                    img { src: asset!("/assets/setup_icon.png"), style: "{icon_style}", alt: "" }
                }
                "Setup"
            }

            // View button
            button {
                class: "btn btn-ghost flex-col gap-1 w-1/3 h-14 text-xs",
                aria_label: "View {label} auto-seed schedule",
                onclick: {
                    let is_eu = is_eu;
                    move |_| {
                        spawn(async move {
                            match autoseed::view_autoseed_schedule(is_eu).await {
                                Ok(result) => {
                                    show_alert(&result, "Auto-Seed Schedule").await;
                                }
                                Err(e) => {
                                    error!("Failed to view autoseed schedule: {}", e);
                                    show_alert(
                                        "Failed to view autoseed schedule. Check the logs for details.",
                                        "Error",
                                    ).await;
                                }
                            }
                        });
                    }
                },
                img { src: asset!("/assets/view.svg"), style: "{icon_style}", alt: "" }
                "View"
            }

            // Uninstall button
            button {
                class: "btn btn-ghost flex-col gap-1 w-1/3 h-14 text-xs",
                aria_label: "Uninstall {label} auto-seed",
                onclick: {
                    let is_eu = is_eu;
                    let label_owned = if is_eu { "EU servers" } else { "auto-seed" };
                    let refresh_autoseed = refresh_autoseed.clone();
                    move |_| {
                        let refresh_autoseed = refresh_autoseed.clone();
                        spawn(async move {
                            let confirmed = show_confirm(
                                &format!("Are you sure you want to uninstall the {} scheduled task?", label_owned),
                                "Uninstall",
                            ).await;
                            if !confirmed {
                                return;
                            }
                            let result = if is_eu {
                                autoseed::uninstall_auto_seed_eu().await
                            } else {
                                autoseed::uninstall_auto_seed().await
                            };
                            match result {
                                Ok(true) => {
                                    show_alert(
                                        &format!("Uninstalled {} scheduled task", label_owned),
                                        "Success",
                                    ).await;
                                }
                                Ok(false) => {
                                    let prefix = if is_eu { "EU " } else { "" };
                                    show_alert(
                                        &format!("No {}scheduled task found to uninstall", prefix),
                                        "Info",
                                    ).await;
                                }
                                Err(e) => {
                                    error!("Failed to uninstall {} task: {}", label_owned, e);
                                    show_alert(
                                        &format!("Failed to uninstall {} task. Check the logs for details.", label_owned),
                                        "Error",
                                    ).await;
                                }
                            }
                            refresh_autoseed();
                        });
                    }
                },
                img { src: asset!("/assets/remove.png"), style: "{icon_style}", alt: "" }
                "Uninstall"
            }
        }
    }
}

/// Auto-seed setup flow with time prompt
async fn setup_auto_seed_flow(is_eu: bool, mut setup_loading: Signal<bool>) {
    let config = if is_eu { &EU_CONFIG } else { &NA_CONFIG };

    let local_display = utc_to_local_display(config.default_h, config.default_m);
    let tz = get_timezone_name();

    let prompt_message = format!(
        "Enter the time for {} auto-seed in UTC (24h format):\n\n{} UTC = {} your time ({}){}",
        config.label, config.default_utc, local_display, tz, config.extra_prompt
    );

    let time_input = show_prompt(&prompt_message, config.default_utc, "Auto-Seed Setup").await;

    let time_input = match time_input {
        Some(t) => t,
        None => return,
    };

    let trimmed = time_input.trim().to_string();

    // Parse HH:MM format
    let caps = match AUTO_SEED_TIME_REGEX.captures(&trimmed) {
        Some(c) => c,
        None => {
            show_alert(
                &format!("Invalid time format. Please use HH:MM (e.g., {})", config.default_utc),
                "Invalid Time",
            ).await;
            return;
        }
    };

    let utc_hours: u32 = caps[1].parse().unwrap_or(0);
    let utc_minutes: u32 = caps[2].parse().unwrap_or(0);

    if utc_hours > 23 || utc_minutes > 59 {
        show_alert(
            "Invalid time. Hours must be 0-23, minutes must be 0-59.",
            "Invalid Time",
        ).await;
        return;
    }

    let (local_h, local_m) = utc_to_local(utc_hours, utc_minutes);
    let time = format!("{:02}:{:02}:00", local_h, local_m);

    setup_loading.set(true);

    // Store the chosen time
    if let Err(e) = store_session(config.store_key, trimmed.clone()) {
        error!("Failed to store auto-seed time: {}", e);
    }

    // Run the setup
    let result = if is_eu {
        autoseed::setup_auto_seed_2(time).await
    } else {
        autoseed::setup_auto_seed(time).await
    };

    match result {
        Ok(msg) => {
            show_alert(&msg, "Auto-Seed Setup").await;
        }
        Err(e) => {
            error!("{} auto-seed setup failed: {}", config.label, e);
            show_alert(
                &format!(
                    "Failed to set up {} auto-seed scheduled task. Check the logs for details.",
                    config.label
                ),
                "Error",
            ).await;
        }
    }

    setup_loading.set(false);
}

/// Generate a simple UUID v4-like string using rand
fn generate_uuid() -> String {
    use rand::Rng;
    let mut rng = rand::thread_rng();
    let bytes: [u8; 16] = rng.gen();
    format!(
        "{:02x}{:02x}{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
        bytes[0], bytes[1], bytes[2], bytes[3],
        bytes[4], bytes[5],
        (bytes[6] & 0x0f) | 0x40, bytes[7],
        (bytes[8] & 0x3f) | 0x80, bytes[9],
        bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15],
    )
}
