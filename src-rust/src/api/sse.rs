use std::error::Error as _;
use std::pin::Pin;

use futures::StreamExt;
use rand::Rng;
use reqwest_eventsource::{Event, EventSource};
use tokio::sync::Notify;

use crate::api::client::{api_base_url, get_api_key_val, get_token, refresh_auth, HTTP_CLIENT};
use crate::api::types::BatchStatsResult;
use crate::state::servers::{SSE_CONNECTED, SSE_FAILURE_COUNT};

use once_cell::sync::Lazy;

/// Notify handle to force an immediate SSE reconnect (e.g. manual refresh, wake)
static RECONNECT_NOTIFY: Lazy<Notify> = Lazy::new(Notify::new);

/// Notify handle to wake the polling fallback loop when SSE disconnects
static POLL_NOTIFY: Lazy<Notify> = Lazy::new(Notify::new);

/// Notify handle to wake the monitor loop when SSE reconnects
static MONITOR_NOTIFY: Lazy<Notify> = Lazy::new(Notify::new);

/// Signal the SSE loop to drop its connection and reconnect immediately.
pub fn request_reconnect() {
    RECONNECT_NOTIFY.notify_one();
}

/// Signal the polling loop that SSE has disconnected and it should poll soon.
pub fn request_poll() {
    POLL_NOTIFY.notify_one();
}

/// Wait until SSE signals a disconnect (for use in the polling loop's select!).
pub async fn poll_notified() {
    POLL_NOTIFY.notified().await;
}

/// Wake the monitor loop so it re-checks the seeding candidate immediately.
fn notify_monitor() {
    MONITOR_NOTIFY.notify_waiters();
}

/// Wait until SSE signals a reconnect (for use in the monitor loop's select!).
pub async fn monitor_notified() {
    MONITOR_NOTIFY.notified().await;
}

fn reset_keepalive() -> Pin<Box<tokio::time::Sleep>> {
    Box::pin(tokio::time::sleep(std::time::Duration::from_secs(60)))
}

/// Long-running SSE loop. Must be spawned inside the Dioxus runtime
/// (via `dioxus::prelude::spawn`) so that signal writes work.
pub async fn run_sse_loop() {
    let mut failures: u32 = 0;

    loop {
        let url = format!("{}/api/servers/stats/stream", api_base_url());

        let mut builder = HTTP_CLIENT
            .get(&url)
            .timeout(std::time::Duration::from_secs(86400));
        if let Some(ref t) = get_token() {
            builder = builder.bearer_auth(t);
        } else if let Some(ref k) = get_api_key_val() {
            builder = builder.header("x-api-key", k.as_str());
        }

        let mut es = match EventSource::new(builder) {
            Ok(es) => es,
            Err(e) => {
                tracing::error!("Failed to build EventSource: {e}");
                failures += 1;
                let backoff_secs = std::cmp::min(1u64 << (failures - 1), 300);
                tokio::time::sleep(std::time::Duration::from_secs(backoff_secs)).await;
                continue;
            }
        };
        let mut keepalive_timeout = reset_keepalive();
        let mut keepalive_started = std::time::Instant::now();

        loop {
            tokio::select! {
                maybe_event = es.next() => {
                    match maybe_event {
                        Some(Ok(Event::Open)) => {
                            tracing::info!("SSE connected");
                            *SSE_CONNECTED.write() = true;
                            *SSE_FAILURE_COUNT.write() = 0;
                            failures = 0;
                            keepalive_timeout = reset_keepalive();
                            keepalive_started = std::time::Instant::now();
                            // Invalidate seeding status cache so monitor re-fetches via HTTP,
                            // catching any seeding_status events lost during the disconnect.
                            crate::backend::api_client::invalidate_seeding_status_cache();
                            notify_monitor();
                        }
                        Some(Ok(Event::Message(msg))) => {
                            keepalive_timeout = reset_keepalive();
                            keepalive_started = std::time::Instant::now();

                            if msg.event == "stats" {
                                match serde_json::from_str::<Vec<BatchStatsResult>>(&msg.data) {
                                    Ok(stats) => {
                                        crate::app::apply_stats_update(stats);
                                    }
                                    Err(e) => {
                                        tracing::warn!("SSE stats parse error: {e}");
                                    }
                                }
                            } else if msg.event == "seeding_status" {
                                match serde_json::from_str::<crate::api::types::SeedingStatusResponse>(&msg.data) {
                                    Ok(status) => {
                                        crate::backend::api_client::update_seeding_status_cache(status);
                                    }
                                    Err(e) => {
                                        tracing::warn!("SSE seeding_status parse error: {e}");
                                    }
                                }
                            }
                            // heartbeat events are just keepalive — no action needed
                        }
                        Some(Err(reqwest_eventsource::Error::InvalidStatusCode(status, resp))) => {
                            let content_type = resp.headers()
                                .get(reqwest::header::CONTENT_TYPE)
                                .and_then(|v| v.to_str().ok())
                                .unwrap_or("(none)")
                                .to_string();
                            // Extract Retry-After before consuming body
                            let retry_after = if status == reqwest::StatusCode::TOO_MANY_REQUESTS {
                                resp.headers()
                                    .get(reqwest::header::RETRY_AFTER)
                                    .and_then(|v| v.to_str().ok())
                                    .and_then(|s| s.parse::<u64>().ok())
                                    .filter(|&secs| secs > 0 && secs <= 120)
                            } else {
                                None
                            };
                            let body_preview = resp.text().await
                                .map(|b| b.chars().take(128).collect::<String>())
                                .unwrap_or_default();
                            tracing::warn!(
                                "SSE error status: {status} content-type={content_type} body={body_preview:?}"
                            );
                            *SSE_CONNECTED.write() = false;
                            request_poll();
                            es.close();

                            if status == reqwest::StatusCode::UNAUTHORIZED {
                                if let Err(e) = refresh_auth().await {
                                    tracing::warn!("SSE auth refresh failed: {e}");
                                }
                            }

                            if let Some(secs) = retry_after {
                                // Server told us exactly how long to wait — skip
                                // exponential backoff and wait the full window so
                                // we don't burn rate-limit slots with premature retries.
                                *SSE_FAILURE_COUNT.write() = failures;
                                let jitter: u64 = rand::thread_rng().gen_range(1..=3);
                                let wait = secs + jitter;
                                tracing::info!(
                                    "SSE rate-limited, waiting {wait}s (Retry-After: {secs}s + {jitter}s jitter, failure #{failures})"
                                );
                                tokio::time::sleep(std::time::Duration::from_secs(wait)).await;
                                // Don't increment failures further — we respected the server's window
                                continue;
                            }

                            failures += 1;
                            break;
                        }
                        Some(Err(reqwest_eventsource::Error::InvalidContentType(header, resp))) => {
                            let content_type = header.to_str().unwrap_or("(non-ascii)");
                            let body_preview = resp.text().await
                                .map(|b| b.chars().take(128).collect::<String>())
                                .unwrap_or_default();
                            tracing::warn!(
                                "SSE unexpected content-type: {content_type:?} body={body_preview:?}"
                            );
                            *SSE_CONNECTED.write() = false;
                            request_poll();
                            es.close();
                            failures += 1;
                            break;
                        }
                        Some(Err(reqwest_eventsource::Error::Transport(e))) => {
                            tracing::warn!(
                                "SSE transport error: {e} (is_timeout={} is_connect={} is_body={})",
                                e.is_timeout(), e.is_connect(), e.is_body()
                            );
                            if let Some(source) = e.source() {
                                tracing::warn!("SSE transport cause: {source}");
                            }
                            *SSE_CONNECTED.write() = false;
                            request_poll();
                            es.close();
                            failures += 1;
                            break;
                        }
                        Some(Err(e)) => {
                            tracing::warn!("SSE error ({kind}): {e}",
                                kind = match &e {
                                    reqwest_eventsource::Error::Utf8(_) => "utf8",
                                    reqwest_eventsource::Error::Parser(_) => "parser",
                                    reqwest_eventsource::Error::InvalidLastEventId(_) => "last-event-id",
                                    reqwest_eventsource::Error::StreamEnded => "stream-ended",
                                    _ => "unknown",
                                }
                            );
                            *SSE_CONNECTED.write() = false;
                            request_poll();
                            es.close();
                            failures += 1;
                            break;
                        }
                        None => {
                            // Stream ended
                            tracing::info!("SSE stream ended");
                            *SSE_CONNECTED.write() = false;
                            request_poll();
                            failures += 1;
                            break;
                        }
                    }
                }
                _ = &mut keepalive_timeout => {
                    let ka_elapsed = keepalive_started.elapsed();
                    *SSE_CONNECTED.write() = false;
                    es.close();

                    if ka_elapsed > std::time::Duration::from_secs(120) {
                        // System likely woke from sleep — wait for network
                        tracing::info!(
                            "SSE keepalive timeout after wake (elapsed {:.0}s), waiting 3s for network",
                            ka_elapsed.as_secs_f64()
                        );
                        tokio::time::sleep(std::time::Duration::from_secs(3)).await;
                        failures = 0;
                        *SSE_FAILURE_COUNT.write() = 0;
                    } else {
                        tracing::info!("SSE keepalive timeout (60s), reconnecting");
                        request_poll();
                        failures += 1;
                    }
                    break;
                }
                _ = RECONNECT_NOTIFY.notified() => {
                    tracing::info!("SSE reconnect requested");
                    *SSE_CONNECTED.write() = false;
                    es.close();
                    failures = 1; // 1s cooldown to prevent rate-limit thrashing
                    *SSE_FAILURE_COUNT.write() = 0;
                    break;
                }
            }
        }

        // Exponential backoff: 1s → 2s → 4s → 8s → 16s → 32s → 64s → 128s → 256s → 300s cap
        // Backs off aggressively once polling fallback is handling data delivery.
        // Manual reconnect button and system-wake both reset failures to 0.
        let backoff_secs = if failures == 0 {
            0
        } else {
            std::cmp::min(1u64 << (failures - 1), 300)
        };

        if backoff_secs > 0 {
            *SSE_FAILURE_COUNT.write() = failures;
            // Add jitter to prevent thundering herd when many clients reconnect simultaneously
            let jitter: u64 = rand::thread_rng().gen_range(0..=std::cmp::min(backoff_secs, 3));
            let wait = backoff_secs + jitter;
            tracing::info!("SSE reconnecting in {wait}s (failure #{failures})");
            tokio::time::sleep(std::time::Duration::from_secs(wait)).await;
        }
    }
}
