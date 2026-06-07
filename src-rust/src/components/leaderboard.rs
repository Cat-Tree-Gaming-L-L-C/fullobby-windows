use dioxus::prelude::*;

use crate::api::types::UserSeedingStats;
use crate::state::auth::{IS_LOGGED_IN, USER};

/// Selected time period for the leaderboard
static LEADERBOARD_PERIOD: GlobalSignal<i64> = Signal::global(|| 7);

/// Cached leaderboard data
static LEADERBOARD_DATA: GlobalSignal<Vec<crate::api::types::LeaderboardEntry>> =
    Signal::global(Vec::new);

/// Whether a fetch is in progress (starts true so first render shows spinner, not "no data")
static LEADERBOARD_LOADING: GlobalSignal<bool> = Signal::global(|| true);

/// Error message from last fetch
static LEADERBOARD_ERROR: GlobalSignal<Option<String>> = Signal::global(|| None);

/// My Stats data
static MY_STATS_DATA: GlobalSignal<Option<UserSeedingStats>> = Signal::global(|| None);

/// Whether My Stats fetch is in progress (starts true to avoid flash of "no stats")
static MY_STATS_LOADING: GlobalSignal<bool> = Signal::global(|| true);

/// Leaderboard page: displays seeding time rankings
#[component]
pub fn Leaderboard() -> Element {
    let loading = *LEADERBOARD_LOADING.read();
    let error = LEADERBOARD_ERROR.read().clone();
    let entries = LEADERBOARD_DATA.read();
    let period = *LEADERBOARD_PERIOD.read();
    let logged_in = *IS_LOGGED_IN.read();

    // Fetch leaderboard on mount and when period changes
    use_effect(move || {
        let days = *LEADERBOARD_PERIOD.read();
        spawn(async move {
            fetch_leaderboard(days).await;
        });
    });

    // Fetch my stats when period or login state changes
    use_effect(move || {
        let days = *LEADERBOARD_PERIOD.read();
        let logged_in = *IS_LOGGED_IN.read();
        if logged_in {
            spawn(async move {
                fetch_my_stats(days).await;
            });
        } else {
            *MY_STATS_DATA.write() = None;
        }
    });

    let period_label = match period {
        1 => "Today",
        7 => "This Week",
        30 => "This Month",
        _ => "All Time",
    };

    rsx! {
        div { class: "flex flex-col w-full h-full p-4 gap-3 overflow-y-auto",
            // Header with period selector
            div { class: "flex items-center justify-between",
                h2 { class: "text-lg font-bold", "Leaderboard — {period_label}" }
                div { class: "flex gap-1",
                    PeriodButton { label: "Day", days: 1, current: period }
                    PeriodButton { label: "Week", days: 7, current: period }
                    PeriodButton { label: "Month", days: 30, current: period }
                    PeriodButton { label: "All", days: 3650, current: period }
                }
            }

            // My Stats card (only when logged in)
            if logged_in {
                { render_my_stats_card() }
            }

            // Content
            if loading && entries.is_empty() {
                div { class: "flex flex-col items-center justify-center w-full h-32 gap-2",
                    span { class: "loading loading-spinner loading-md" }
                    span { class: "text-sm opacity-70", "Loading leaderboard..." }
                }
            } else if let Some(err) = error {
                div { class: "flex flex-col items-center justify-center w-full h-32 gap-2",
                    span { class: "text-error text-sm", "{err}" }
                    button {
                        class: "btn btn-ghost btn-sm underline",
                        onclick: move |_| {
                            let days = *LEADERBOARD_PERIOD.read();
                            spawn(async move { fetch_leaderboard(days).await; });
                        },
                        "Tap to retry"
                    }
                }
            } else if entries.is_empty() {
                div { class: "flex items-center justify-center w-full h-32",
                    span { class: "text-sm opacity-70", "No seeding data for this period." }
                }
            } else {
                // Leaderboard table
                div { class: "overflow-x-auto",
                    table { class: "table table-sm w-full",
                        thead {
                            tr {
                                th { class: "w-10", "#" }
                                th { "Seeder" }
                                th { class: "text-right", "Time" }
                                th { class: "text-right", "Sessions" }
                            }
                        }
                        tbody {
                            for entry in entries.iter() {
                                {
                                    let hours = entry.total_time_secs / 3600;
                                    let mins = (entry.total_time_secs % 3600) / 60;
                                    let rank_class = match entry.rank {
                                        1 => "text-warning font-bold",
                                        2 => "text-secondary font-semibold",
                                        3 => "text-accent font-semibold",
                                        _ => "",
                                    };
                                    rsx! {
                                        tr { key: "{entry.user_id}",
                                            td { class: "{rank_class}", "{entry.rank}" }
                                            td { class: "{rank_class}", "{entry.username}" }
                                            td { class: "text-right tabular-nums", "{hours}h {mins}m" }
                                            td { class: "text-right tabular-nums", "{entry.session_count}" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

#[component]
fn PeriodButton(label: &'static str, days: i64, current: i64) -> Element {
    let active = current == days;
    rsx! {
        button {
            class: if active { "btn btn-xs btn-primary" } else { "btn btn-xs btn-ghost" },
            onclick: move |_| *LEADERBOARD_PERIOD.write() = days,
            "{label}"
        }
    }
}

/// Format seconds into "Xh Ym" display
fn fmt_duration(secs: i64) -> String {
    let h = secs / 3600;
    let m = (secs % 3600) / 60;
    if h > 0 { format!("{}h {}m", h, m) } else { format!("{}m", m) }
}

/// Render the "My Stats" card
fn render_my_stats_card() -> Element {
    let stats_loading = *MY_STATS_LOADING.read();
    let stats = MY_STATS_DATA.read();

    rsx! {
        div { class: "bg-base-200 rounded-box p-3",
            div { class: "text-sm font-semibold flex items-center gap-2 mb-2",
                "My Stats"
                if stats_loading {
                    span { class: "loading loading-spinner loading-xs" }
                }
            }

            if let Some(ref s) = *stats {
                // Summary row
                div { class: "flex gap-2 mb-3",
                    div { class: "flex-1 bg-base-100 rounded-lg p-2",
                        div { class: "text-xs opacity-60", "Total Time" }
                        div { class: "text-sm font-semibold tabular-nums", "{fmt_duration(s.total_time_secs)}" }
                    }
                    div { class: "flex-1 bg-base-100 rounded-lg p-2",
                        div { class: "text-xs opacity-60", "Sessions" }
                        div { class: "text-sm font-semibold tabular-nums", "{s.session_count}" }
                    }
                    div { class: "flex-1 bg-base-100 rounded-lg p-2",
                        div { class: "text-xs opacity-60", "Avg Session" }
                        div { class: "text-sm font-semibold tabular-nums", "{fmt_duration(s.avg_session_secs)}" }
                    }
                }

                // Per-server breakdown
                if !s.servers.is_empty() {
                    div { class: "overflow-x-auto mb-3",
                        table { class: "table table-xs w-full",
                            thead {
                                tr {
                                    th { "Server" }
                                    th { class: "text-right", "Time" }
                                    th { class: "text-right", "Sessions" }
                                }
                            }
                            tbody {
                                for srv in s.servers.iter() {
                                    tr { key: "{srv.server_name}",
                                        td { class: "truncate max-w-[10rem]", "{srv.server_name}" }
                                        td { class: "text-right tabular-nums", "{fmt_duration(srv.total_time_secs)}" }
                                        td { class: "text-right tabular-nums", "{srv.session_count}" }
                                    }
                                }
                            }
                        }
                    }
                }

                // Recent sessions (last 5)
                if !s.recent_sessions.is_empty() {
                    div { class: "text-xs font-semibold mb-1", "Recent Sessions" }
                    div { class: "flex flex-col gap-1",
                        for sess in s.recent_sessions.iter().take(5) {
                            {
                                let badge_class = match sess.status.as_str() {
                                    "completed" => "badge-success",
                                    "active" => "badge-info",
                                    _ => "badge-warning",
                                };
                                rsx! {
                                    div { key: "{sess.session_id}", class: "flex items-center justify-between text-xs bg-base-100 rounded px-2 py-1",
                                        span { class: "truncate max-w-[8rem]", "{sess.server_name}" }
                                        div { class: "flex items-center gap-2",
                                            span { class: "badge badge-xs {badge_class}", "{sess.status}" }
                                            span { class: "tabular-nums", "{fmt_duration(sess.duration_secs)}" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            } else if !stats_loading {
                span { class: "text-xs opacity-70", "No stats available for this period." }
            }
        }
    }
}

async fn fetch_my_stats(days: i64) {
    let user_id = match USER.read().as_ref() {
        Some(u) => u.user_id.clone(),
        None => return,
    };

    if crate::state::cooldown::is_on_cooldown("my_stats") {
        return;
    }
    crate::state::cooldown::set_cooldown("my_stats", 5);

    *MY_STATS_LOADING.write() = true;

    match crate::api::client::get_user_stats(&user_id, days).await {
        Ok(stats) => {
            *MY_STATS_DATA.write() = Some(stats);
        }
        Err(e) => {
            tracing::error!("Failed to fetch my stats: {}", e);
            *MY_STATS_DATA.write() = None;
        }
    }

    *MY_STATS_LOADING.write() = false;
}

async fn fetch_leaderboard(days: i64) {
    if crate::state::cooldown::is_on_cooldown("leaderboard") {
        return;
    }
    crate::state::cooldown::set_cooldown("leaderboard", 5);

    *LEADERBOARD_LOADING.write() = true;
    *LEADERBOARD_ERROR.write() = None;

    match crate::api::client::get_leaderboard(days, 25).await {
        Ok(entries) => {
            *LEADERBOARD_DATA.write() = entries;
        }
        Err(e) => {
            tracing::error!("Failed to fetch leaderboard: {}", e);
            let msg = crate::api::client::friendly_error(e.as_ref());
            *LEADERBOARD_ERROR.write() = Some(format!("Failed to load leaderboard: {}", msg));
        }
    }

    *LEADERBOARD_LOADING.write() = false;
}
