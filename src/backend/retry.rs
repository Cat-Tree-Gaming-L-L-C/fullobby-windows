use std::time::Duration;
use tracing::warn;

/// Send an HTTP request with retry on transient network errors.
///
/// Takes a closure that builds a fresh `RequestBuilder` each attempt
/// (since `.send()` consumes the builder). Retries up to 3 total attempts
/// with exponential backoff (1s, 2s) on connect/timeout errors and
/// retryable status codes (429, 503).
pub async fn send_with_retry(
    build: impl Fn() -> reqwest::RequestBuilder,
) -> Result<reqwest::Response, reqwest::Error> {
    send_with_retry_inner(build, false).await
}

/// Like `send_with_retry`, but shows a toast notification to the user on each retry.
/// Use this for user-initiated requests where retry visibility matters.
pub async fn send_with_retry_notify(
    build: impl Fn() -> reqwest::RequestBuilder,
) -> Result<reqwest::Response, reqwest::Error> {
    send_with_retry_inner(build, true).await
}

/// Returns true for HTTP status codes that are safe to retry.
fn is_retryable_status(status: reqwest::StatusCode) -> bool {
    status == reqwest::StatusCode::TOO_MANY_REQUESTS
        || status == reqwest::StatusCode::SERVICE_UNAVAILABLE
}

/// Parse the `Retry-After` header value as seconds.
/// Supports integer seconds only (not HTTP-date format).
fn parse_retry_after(response: &reqwest::Response) -> Option<u64> {
    response
        .headers()
        .get(reqwest::header::RETRY_AFTER)
        .and_then(|v| v.to_str().ok())
        .and_then(|s| s.parse::<u64>().ok())
        .filter(|&secs| secs > 0 && secs <= 60) // cap at 60s to avoid unreasonable waits
}

#[cfg(test)]
mod tests {
    use super::*;

    // ── is_retryable_status ────────────────────────────────────

    #[test]
    fn test_retryable_429() {
        assert!(is_retryable_status(reqwest::StatusCode::TOO_MANY_REQUESTS));
    }

    #[test]
    fn test_retryable_503() {
        assert!(is_retryable_status(reqwest::StatusCode::SERVICE_UNAVAILABLE));
    }

    #[test]
    fn test_not_retryable_200() {
        assert!(!is_retryable_status(reqwest::StatusCode::OK));
    }

    #[test]
    fn test_not_retryable_404() {
        assert!(!is_retryable_status(reqwest::StatusCode::NOT_FOUND));
    }

    #[test]
    fn test_not_retryable_500() {
        assert!(!is_retryable_status(reqwest::StatusCode::INTERNAL_SERVER_ERROR));
    }

    #[test]
    fn test_not_retryable_401() {
        assert!(!is_retryable_status(reqwest::StatusCode::UNAUTHORIZED));
    }

    #[test]
    fn test_not_retryable_400() {
        assert!(!is_retryable_status(reqwest::StatusCode::BAD_REQUEST));
    }

    #[test]
    fn test_not_retryable_502() {
        assert!(!is_retryable_status(reqwest::StatusCode::BAD_GATEWAY));
    }

    // ── parse_retry_after ──────────────────────────────────────

    fn make_response_with_retry_after(value: &str) -> reqwest::Response {
        http::Response::builder()
            .status(429)
            .header(reqwest::header::RETRY_AFTER, value)
            .body("")
            .unwrap()
            .into()
    }

    fn make_response_without_retry_after() -> reqwest::Response {
        http::Response::builder()
            .status(429)
            .body("")
            .unwrap()
            .into()
    }

    #[test]
    fn test_parse_retry_after_valid() {
        let resp = make_response_with_retry_after("5");
        assert_eq!(parse_retry_after(&resp), Some(5));
    }

    #[test]
    fn test_parse_retry_after_missing() {
        let resp = make_response_without_retry_after();
        assert_eq!(parse_retry_after(&resp), None);
    }

    #[test]
    fn test_parse_retry_after_zero() {
        let resp = make_response_with_retry_after("0");
        assert_eq!(parse_retry_after(&resp), None);
    }

    #[test]
    fn test_parse_retry_after_capped_above_60() {
        let resp = make_response_with_retry_after("120");
        assert_eq!(parse_retry_after(&resp), None);
    }

    #[test]
    fn test_parse_retry_after_at_60() {
        let resp = make_response_with_retry_after("60");
        assert_eq!(parse_retry_after(&resp), Some(60));
    }

    #[test]
    fn test_parse_retry_after_non_numeric() {
        let resp = make_response_with_retry_after("abc");
        assert_eq!(parse_retry_after(&resp), None);
    }

    #[test]
    fn test_parse_retry_after_negative() {
        let resp = make_response_with_retry_after("-1");
        assert_eq!(parse_retry_after(&resp), None); // parse::<u64> fails on negative
    }

    #[test]
    fn test_parse_retry_after_one() {
        let resp = make_response_with_retry_after("1");
        assert_eq!(parse_retry_after(&resp), Some(1));
    }
}

async fn send_with_retry_inner(
    build: impl Fn() -> reqwest::RequestBuilder,
    notify: bool,
) -> Result<reqwest::Response, reqwest::Error> {
    const MAX_ATTEMPTS: u32 = 3;
    const BACKOFF: [u64; 2] = [1, 2];

    let mut attempt = 0u32;
    loop {
        attempt += 1;
        match build().send().await {
            Ok(resp) if attempt < MAX_ATTEMPTS && is_retryable_status(resp.status()) => {
                let backoff = BACKOFF[std::cmp::min(attempt as usize - 1, BACKOFF.len() - 1)];
                let delay = parse_retry_after(&resp).unwrap_or(backoff);
                warn!(
                    "Retryable status {} (attempt {}/{}), retrying in {}s",
                    resp.status(),
                    attempt,
                    MAX_ATTEMPTS,
                    delay
                );
                if notify {
                    crate::state::toast::add_toast(
                        &format!("Server busy, retrying... ({}/{})", attempt, MAX_ATTEMPTS),
                        crate::state::toast::ToastType::Info,
                        Some(2000),
                    );
                }
                tokio::time::sleep(Duration::from_secs(delay)).await;
            }
            Ok(resp) => return Ok(resp),
            Err(e) if attempt < MAX_ATTEMPTS && (e.is_connect() || e.is_timeout()) => {
                let delay = BACKOFF[std::cmp::min(attempt as usize - 1, BACKOFF.len() - 1)];
                warn!(
                    "Transient network error (attempt {}/{}), retrying in {}s: {}",
                    attempt, MAX_ATTEMPTS, delay, e
                );
                if notify {
                    crate::state::toast::add_toast(
                        &format!("Connection issue, retrying... ({}/{})", attempt, MAX_ATTEMPTS),
                        crate::state::toast::ToastType::Info,
                        Some(2000),
                    );
                }
                tokio::time::sleep(Duration::from_secs(delay)).await;
            }
            Err(e) => return Err(e),
        }
    }
}
