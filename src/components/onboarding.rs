use dioxus::prelude::*;
use tracing::error;

use crate::backend::session::store_session;
use crate::state::auth::{
    AuthProvider, IS_GUEST,
    ONBOARDING_COMPLETE, ONBOARDING_STEP, USER, AUTH_PROVIDER,
};
use crate::state::toast::{add_toast, ToastType};

/// Full-screen onboarding wizard overlay
///
/// Flow:
///   Step 0 — Sign in (Steam / Discord / Guest)
///   Step 1 — Optional account linking (Steam + Discord for session tracking)
///   Step 2 — Set Nickname (with random anonymous option)
///   Step 3 — Done
#[component]
pub fn Onboarding() -> Element {
    let step = *ONBOARDING_STEP.read();

    rsx! {
        dialog {
            class: "modal absolute modal-open",
            style: "width: 100vw; height: 100vh; z-index: 1000;",
            div { class: "modal-box absolute inset-0 w-full h-full max-w-none flex flex-col items-center justify-center overflow-y-auto",
                // Steps indicator
                ul { class: "steps mb-4",
                    li { class: "step step-primary", "Sign In" }
                    li { class: if step >= 1 { "step step-primary" } else { "step" }, "Link" }
                    li { class: if step >= 2 { "step step-primary" } else { "step" }, "Nickname" }
                    li { class: if step >= 3 { "step step-primary" } else { "step" }, "Done" }
                }

                match step {
                    0 => rsx! { StepSignIn {} },
                    1 => rsx! { StepLinkAccounts {} },
                    2 => rsx! { StepNickname {} },
                    3 => rsx! { StepComplete {} },
                    _ => rsx! { StepSignIn {} },
                }
            }
        }
    }
}

/// Step 0: Sign in with a provider
#[component]
fn StepSignIn() -> Element {
    let mut loading_provider = use_signal(|| None::<String>);

    rsx! {
        img { src: asset!("/assets/logo_nbg.png"), class: "h-16 w-16 mb-2" }
        h3 { class: "font-bold text-2xl mb-2", "Welcome to Esprit Seeder!" }
        p { class: "text-center mb-6 opacity-70 max-w-sm",
            "Sign in to track your seeding contributions, or continue as a guest."
        }

        div { class: "flex flex-col gap-3 w-full max-w-xs",
            // Steam
            button {
                class: "btn btn-primary w-full gap-2",
                disabled: loading_provider.read().is_some(),
                onclick: move |_| {
                    loading_provider.set(Some("steam".to_string()));
                    open_provider_login("steam");
                    loading_provider.set(None);
                },
                if loading_provider.read().as_deref() == Some("steam") {
                    span { class: "loading loading-spinner loading-sm" }
                }
                "Sign in with Steam"
            }
            // Discord
            button {
                class: "btn w-full gap-2",
                disabled: loading_provider.read().is_some(),
                onclick: move |_| {
                    loading_provider.set(Some("discord".to_string()));
                    open_provider_login("discord");
                    loading_provider.set(None);
                },
                if loading_provider.read().as_deref() == Some("discord") {
                    span { class: "loading loading-spinner loading-sm" }
                }
                "Sign in with Discord"
            }

            div { class: "divider text-xs my-0", "or" }

            button {
                class: "btn btn-ghost btn-xs w-full opacity-60",
                disabled: loading_provider.read().is_some(),
                onclick: move |_| {
                    spawn(async move {
                        loading_provider.set(Some("guest".to_string()));
                        match crate::api::client::register_guest().await {
                            Ok(resp) => {
                                // Store credentials
                                crate::api::client::set_api_key(&resp.api_key);
                                let _ = store_session("api_key", resp.api_key);
                                let _ = store_session("user_id", resp.user_id.clone());
                                let _ = store_session("guest_mode", "true".to_string());
                                let _ = store_session("auth_provider", "guest".to_string());

                                // Set auth signals
                                *IS_GUEST.write() = true;
                                *crate::state::auth::IS_LOGGED_IN.write() = true;
                                *crate::state::auth::AUTH_METHOD.write() = crate::state::auth::AuthMethod::ApiKey;
                                *AUTH_PROVIDER.write() = Some(AuthProvider::Guest);
                                *USER.write() = Some(crate::state::auth::UserInfo {
                                    user_id: resp.user_id,
                                    username: resp.username,
                                    auth_provider: AuthProvider::Guest,
                                    display_name: Some(resp.display_name),
                                    ..Default::default()
                                });

                                complete_onboarding();
                            }
                            Err(e) => {
                                error!("Guest registration failed: {}", e);
                                add_toast(
                                    &crate::api::client::friendly_error(e.as_ref()),
                                    ToastType::Error,
                                    None,
                                );
                            }
                        }
                        loading_provider.set(None);
                    });
                },
                if loading_provider.read().as_deref() == Some("guest") {
                    span { class: "loading loading-spinner loading-sm" }
                }
                "Continue as Guest"
            }
        }
    }
}

/// Open the system browser for OAuth provider login
fn open_provider_login(provider: &str) {
    let state = generate_state();
    crate::state::auth::set_oauth_state(state.clone());
    let url = match crate::api::client::get_oauth_url(provider, &state) {
        Ok(u) => u,
        Err(e) => {
            error!("Invalid OAuth provider '{}': {}", provider, e);
            add_toast(&format!("Invalid login provider: {}", e), ToastType::Error, None);
            return;
        }
    };
    if let Err(e) = open::that(&url) {
        error!("Failed to open browser for {} login: {}", provider, e);
        add_toast(
            &format!("Failed to open browser: {}", e),
            ToastType::Error,
            None,
        );
    }
}

/// Step 1: Optional account linking (Steam + Discord for session tracking)
#[component]
fn StepLinkAccounts() -> Element {
    let user = USER.read();
    let auth_provider = AUTH_PROVIDER.read();
    let mut link_loading = use_signal(|| None::<String>);

    let steam_linked = user.as_ref().map_or(false, |u| u.steam_id.is_some());
    let discord_linked = user.as_ref().map_or(false, |u| u.discord_id.is_some());

    rsx! {
        h3 { class: "font-bold text-xl mb-2", "Link Accounts for Session Tracking" }
        p { class: "text-center mb-6 opacity-70 max-w-sm",
            "Link your Steam and Discord accounts to track your seeding sessions."
        }

        div { class: "flex flex-col gap-3 w-full max-w-xs",
            // Steam — always allow linking (supports multiple Steam accounts)
            if steam_linked {
                div { class: "flex items-center gap-2 px-4 py-3 bg-base-300 rounded-lg",
                    span { class: "text-success text-sm font-medium", "Steam linked" }
                    if *auth_provider == Some(AuthProvider::Steam) {
                        span { class: "badge badge-ghost badge-xs ml-auto", "signed in" }
                    }
                }
            }
            button {
                class: "btn w-full gap-2",
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
                    span { class: "loading loading-spinner loading-sm" }
                }
                if steam_linked { "Link Another Steam Account" } else { "Link Steam Account" }
            }
            if !steam_linked {
                p { class: "text-xs opacity-50 -mt-2 px-1", "Required to verify seeding time" }
            }

            // Discord
            if discord_linked {
                div { class: "flex items-center gap-2 px-4 py-3 bg-base-300 rounded-lg",
                    span { class: "text-success text-sm font-medium", "Discord linked" }
                    if *auth_provider == Some(AuthProvider::Discord) {
                        span { class: "badge badge-ghost badge-xs ml-auto", "signed in" }
                    }
                }
            } else {
                button {
                    class: "btn w-full gap-2",
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
                        span { class: "loading loading-spinner loading-sm" }
                    }
                    "Link Discord Account"
                }
                p { class: "text-xs opacity-50 -mt-2 px-1", "For community features and richer profile" }
            }
        }

        div { class: "mt-6",
            button {
                class: "btn btn-primary btn-sm",
                onclick: move |_| {
                    *ONBOARDING_STEP.write() = 2;
                },
                if steam_linked || discord_linked { "Continue" } else { "Skip for now" }
            }
            if !steam_linked && !discord_linked {
                p { class: "text-xs opacity-40 text-center mt-2", "You can link accounts later from Settings" }
            }
        }
    }
}

/// Step 2: Set a display name / nickname
#[component]
fn StepNickname() -> Element {
    let is_guest = *IS_GUEST.read();
    let user = USER.read();
    let default_name = user
        .as_ref()
        .and_then(|u| u.display_name.clone().or(Some(u.username.clone())))
        .unwrap_or_default();

    let mut name_input = use_signal(|| default_name.clone());
    let mut saving = use_signal(|| false);
    let mut error_msg = use_signal(|| None::<String>);

    rsx! {
        h3 { class: "font-bold text-xl mb-2",
            if is_guest { "Randomize a Nickname" } else { "Choose a Nickname" }
        }
        p { class: "text-center mb-4 opacity-70 max-w-sm",
            if is_guest {
                "Guest accounts use a random anonymous name on the leaderboard."
            } else {
                "Pick a display name for the leaderboard, or go anonymous."
            }
        }

        div { class: "flex flex-col gap-3 w-full max-w-xs",
            // Name input — only for non-guest users
            if !is_guest {
                input {
                    r#type: "text",
                    class: "input input-bordered w-full",
                    placeholder: "Enter nickname...",
                    maxlength: "32",
                    value: "{name_input}",
                    disabled: *saving.read(),
                    oninput: move |e: Event<FormData>| {
                        name_input.set(e.value());
                        error_msg.set(None);
                    },
                }
            }

            if let Some(ref err) = *error_msg.read() {
                p { class: "text-error text-xs -mt-2", "{err}" }
            }

            // Random anonymous nickname button
            button {
                class: if is_guest { "btn btn-primary btn-sm w-full gap-2" } else { "btn btn-ghost btn-sm w-full gap-2" },
                disabled: *saving.read(),
                onclick: move |_| {
                    spawn(async move {
                        if crate::state::cooldown::is_on_cooldown("randomize_name") {
                            return;
                        }
                        crate::state::cooldown::set_cooldown("randomize_name", 3);
                        saving.set(true);
                        error_msg.set(None);
                        match crate::api::client::randomize_display_name().await {
                            Ok(updated) => {
                                name_input.set(updated.display_name.clone().unwrap_or_default());
                                *USER.write() = Some(crate::app::convert_user_info(updated));
                                // No toast during onboarding — the UI already shows the result
                            }
                            Err(e) => {
                                error!("Failed to randomize nickname: {}", e);
                                error_msg.set(Some("Failed to generate nickname".into()));
                            }
                        }
                        saving.set(false);
                    });
                },
                if is_guest { "Randomize Name" } else { "Go Anonymous" }
            }
        }

        div { class: "flex gap-2 mt-6",
            // Skip button
            button {
                class: "btn btn-ghost btn-sm",
                disabled: *saving.read(),
                onclick: move |_| {
                    *ONBOARDING_STEP.write() = 3;
                },
                "Skip"
            }

            // Save button — only for non-guest users (guests use randomize above)
            if !is_guest {
                button {
                    class: "btn btn-primary btn-sm",
                    disabled: *saving.read() || name_input.read().trim().is_empty(),
                    onclick: move |_| {
                        let name = name_input.read().clone();
                        spawn(async move {
                            if crate::state::cooldown::is_on_cooldown("update_name") {
                                return;
                            }
                            crate::state::cooldown::set_cooldown("update_name", 3);
                            saving.set(true);
                            error_msg.set(None);
                            match crate::api::client::update_display_name(&name).await {
                                Ok(updated) => {
                                    *USER.write() = Some(crate::app::convert_user_info(updated));
                                    // No toast during onboarding — advancing to next step is feedback enough
                                    *ONBOARDING_STEP.write() = 3;
                                }
                                Err(e) => {
                                    let msg = e.to_string();
                                    // Extract the error message from API JSON response
                                    let err_text = if let Some(start) = msg.find("\"error\":\"") {
                                        let rest = &msg[start + 9..];
                                        rest.split('"').next().unwrap_or(&msg).to_string()
                                    } else {
                                        msg
                                    };
                                    error!("Failed to save nickname: {}", err_text);
                                    error_msg.set(Some(err_text));
                                }
                            }
                            saving.set(false);
                        });
                    },
                    if *saving.read() {
                        span { class: "loading loading-spinner loading-sm" }
                    }
                    "Save"
                }
            }
        }

        // Leaderboard opt-out toggle
        div { class: "flex items-center gap-3 mt-4 w-full max-w-xs",
            input {
                r#type: "checkbox",
                class: "toggle toggle-primary toggle-sm",
                checked: !USER.read().as_ref().map(|u| u.leaderboard_opt_out).unwrap_or(false),
                disabled: *saving.read(),
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
                            }
                            Err(e) => {
                                error!("Failed to update leaderboard preference: {}", e);
                            }
                        }
                    });
                },
            }
            div { class: "flex flex-col",
                span { class: "text-sm", "Show on Leaderboard" }
                span { class: "text-xs opacity-50", "Appear in public rankings" }
            }
        }

        p { class: "text-xs opacity-40 text-center mt-2", "You can change this anytime from Settings" }
    }
}

/// Step 3: Complete
#[component]
fn StepComplete() -> Element {
    let user = USER.read();
    let is_guest = *IS_GUEST.read();

    rsx! {
        h3 { class: "font-bold text-2xl mb-4", "You're all set!" }

        div { class: "flex flex-col gap-2 mb-6 text-sm",
            if is_guest {
                p { class: "opacity-70", "Mode: Guest" }
            } else if let Some(ref u) = *user {
                p { "Account: " span { class: "font-medium",
                    {u.display_name.as_deref().unwrap_or(&u.username)}
                } }
                if u.steam_id.is_some() {
                    p { class: "text-success", "Steam: Linked" }
                }
                if u.discord_id.is_some() {
                    p { class: "text-success", "Discord: Linked" }
                }
            }
        }

        button {
            class: "btn btn-primary btn-lg",
            onclick: move |_| {
                complete_onboarding();
            },
            "Start Seeding"
        }
    }
}

fn complete_onboarding() {
    *ONBOARDING_COMPLETE.write() = true;
    let _ = store_session("onboarding_complete", "true".to_string());
}

fn generate_state() -> String {
    use rand::Rng;
    let mut rng = rand::thread_rng();
    let bytes: [u8; 16] = rng.gen();
    bytes.iter().map(|b| format!("{:02x}", b)).collect()
}
