use serde::{Deserialize, Serialize};

use crate::backend::server::ServerInfo;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BatchStatsResult {
    pub game: String,
    pub region: String,
    pub index: usize,
    pub map_name: Option<String>,
    pub player_count: Option<i32>,
    pub max_player_count: Option<i32>,
    pub offline: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RegionServers {
    pub na: Vec<ServerInfo>,
    pub eu: Vec<ServerInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ServersResponse {
    pub hll: RegionServers,
    #[serde(default)]
    pub hllv: Option<RegionServers>,
    pub cached_at: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct NextServerResponse {
    pub game: String,
    pub region: String,
    pub index: usize,
    pub server: ServerInfo,
    pub all_exhausted: bool,
    #[serde(default)]
    pub session_id: Option<String>,
}

/// Auth provider used to sign in
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "lowercase")]
pub enum AuthProvider {
    #[default]
    Steam,
    Discord,
    Guest,
}

impl std::fmt::Display for AuthProvider {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Steam => write!(f, "steam"),
            Self::Discord => write!(f, "discord"),
            Self::Guest => write!(f, "guest"),
        }
    }
}

/// User info — supports all auth providers
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct UserInfo {
    pub user_id: String,
    pub username: String,
    #[serde(default)]
    pub auth_provider: AuthProvider,
    #[serde(default)]
    pub display_name: Option<String>,
    #[serde(default)]
    pub steam_id: Option<String>,
    #[serde(default)]
    pub discord_id: Option<String>,
    #[serde(default)]
    pub avatar: Option<String>,
    #[serde(default)]
    pub created_at: u64,
    #[serde(default)]
    pub last_seen_at: u64,
    #[serde(default)]
    pub leaderboard_opt_out: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AuthRefreshResponse {
    pub token: String,
    pub refresh_token: String,
}

/// Response from link-init endpoint — OAuth redirect URL with HMAC-signed state
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LinkInitResponse {
    pub redirect_url: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SteamIdEntry {
    pub id: String,
    pub steam_id: String,
    pub linked_at: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct HeartbeatResponse {
    pub session_id: String,
    pub status: String,
    pub heartbeat_count: u32,
    pub session_duration_secs: u64,
    #[serde(default)]
    pub validated: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StopSessionResponse {
    pub ok: bool,
}

/// Leaderboard entry from seeding stats
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LeaderboardEntry {
    pub rank: i64,
    pub user_id: i64,
    pub username: String,
    pub discord_id: Option<String>,
    pub total_time_secs: i64,
    pub session_count: i64,
}

/// Per-user seeding stats from GET /api/seeding/stats/{user_id}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UserSeedingStats {
    pub total_time_secs: i64,
    pub session_count: i64,
    pub avg_session_secs: i64,
    pub servers: Vec<ServerBreakdown>,
    pub recent_sessions: Vec<RecentSession>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ServerBreakdown {
    pub server_name: String,
    pub total_time_secs: i64,
    pub session_count: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RecentSession {
    pub session_id: String,
    pub server_name: String,
    pub status: String,
    pub started_at: i64,
    pub ended_at: Option<i64>,
    pub duration_secs: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GameSeedingStatus {
    pub na: Option<RegionCandidate>,
    pub eu: Option<RegionCandidate>,
}

/// Pre-computed seeding status (best server per region).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SeedingStatusResponse {
    pub hll: GameSeedingStatus,
    #[serde(default)]
    pub hllv: Option<GameSeedingStatus>,
    pub updated_at: i64,
}

/// A single region's best seeding candidate.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RegionCandidate {
    pub game: String,
    pub index: usize,
    pub server: ServerInfo,
}

/// Response after creating a seeding session.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StartSessionResponse {
    pub session_id: String,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_auth_provider_display() {
        assert_eq!(format!("{}", AuthProvider::Steam), "steam");
        assert_eq!(format!("{}", AuthProvider::Discord), "discord");
    }

    #[test]
    fn test_auth_provider_default() {
        assert_eq!(AuthProvider::default(), AuthProvider::Steam);
    }

    #[test]
    fn test_auth_provider_serde_roundtrip() {
        let steam_json = serde_json::to_string(&AuthProvider::Steam).unwrap();
        assert_eq!(steam_json, "\"steam\"");
        let deserialized: AuthProvider = serde_json::from_str(&steam_json).unwrap();
        assert_eq!(deserialized, AuthProvider::Steam);

        let discord_json = serde_json::to_string(&AuthProvider::Discord).unwrap();
        assert_eq!(discord_json, "\"discord\"");
        let deserialized: AuthProvider = serde_json::from_str(&discord_json).unwrap();
        assert_eq!(deserialized, AuthProvider::Discord);
    }

    #[test]
    fn test_user_info_default() {
        let user = UserInfo::default();
        assert_eq!(user.user_id, "");
        assert_eq!(user.username, "");
        assert_eq!(user.auth_provider, AuthProvider::Steam);
        assert!(user.display_name.is_none());
        assert!(user.steam_id.is_none());
        assert!(user.discord_id.is_none());
        assert!(user.avatar.is_none());
        assert_eq!(user.created_at, 0);
        assert_eq!(user.last_seen_at, 0);
    }

    #[test]
    fn test_user_info_serde_roundtrip() {
        let user = UserInfo {
            user_id: "42".to_string(),
            username: "testuser".to_string(),
            auth_provider: AuthProvider::Discord,
            display_name: Some("Test User".to_string()),
            steam_id: Some("76561198000000000".to_string()),
            discord_id: Some("123456789".to_string()),
            avatar: Some("https://example.com/avatar.png".to_string()),
            created_at: 1700000000,
            last_seen_at: 1700001000,
            leaderboard_opt_out: false,
        };
        let json = serde_json::to_string(&user).unwrap();
        let deserialized: UserInfo = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized, user);
    }

    #[test]
    fn test_user_info_deserialize_minimal() {
        let json = r#"{"user_id":"99","username":"minimal"}"#;
        let user: UserInfo = serde_json::from_str(json).unwrap();
        assert_eq!(user.user_id, "99");
        assert_eq!(user.username, "minimal");
        assert_eq!(user.auth_provider, AuthProvider::Steam);
        assert!(user.display_name.is_none());
        assert!(user.steam_id.is_none());
    }

    #[test]
    fn test_batch_stats_result_serde_roundtrip() {
        let stats = BatchStatsResult {
            game: "hll".to_string(),
            region: "na".to_string(),
            index: 0,
            map_name: Some("Carentan".to_string()),
            player_count: Some(42),
            max_player_count: Some(100),
            offline: false,
            error: None,
        };
        let json = serde_json::to_string(&stats).unwrap();
        let deserialized: BatchStatsResult = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.game, stats.game);
        assert_eq!(deserialized.region, stats.region);
        assert_eq!(deserialized.index, stats.index);
        assert_eq!(deserialized.map_name, stats.map_name);
        assert_eq!(deserialized.player_count, stats.player_count);
        assert_eq!(deserialized.max_player_count, stats.max_player_count);
        assert_eq!(deserialized.offline, stats.offline);
        assert_eq!(deserialized.error, stats.error);
    }

    #[test]
    fn test_batch_stats_result_with_error() {
        let stats = BatchStatsResult {
            game: "hll".to_string(),
            region: "eu".to_string(),
            index: 1,
            map_name: None,
            player_count: None,
            max_player_count: None,
            offline: true,
            error: Some("server unreachable".to_string()),
        };
        let json = serde_json::to_string(&stats).unwrap();
        let deserialized: BatchStatsResult = serde_json::from_str(&json).unwrap();
        assert!(deserialized.offline);
        assert_eq!(deserialized.error, Some("server unreachable".to_string()));
        assert_eq!(deserialized.map_name, None);
    }

    #[test]
    fn test_auth_provider_guest_display() {
        assert_eq!(format!("{}", AuthProvider::Guest), "guest");
    }

    #[test]
    fn test_auth_provider_guest_serde() {
        let json = serde_json::to_string(&AuthProvider::Guest).unwrap();
        assert_eq!(json, "\"guest\"");
        let deserialized: AuthProvider = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized, AuthProvider::Guest);
    }

    #[test]
    fn test_auth_refresh_response_serde() {
        let resp = AuthRefreshResponse {
            token: "new_token_abc".to_string(),
            refresh_token: "new_refresh_xyz".to_string(),
        };
        let json = serde_json::to_string(&resp).unwrap();
        let deserialized: AuthRefreshResponse = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.token, "new_token_abc");
        assert_eq!(deserialized.refresh_token, "new_refresh_xyz");
    }

    #[test]
    fn test_link_init_response_serde() {
        let resp = LinkInitResponse {
            redirect_url: "https://example.com/oauth".to_string(),
        };
        let json = serde_json::to_string(&resp).unwrap();
        let deserialized: LinkInitResponse = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.redirect_url, "https://example.com/oauth");
    }

    #[test]
    fn test_steam_id_entry_serde() {
        let entry = SteamIdEntry {
            id: "1".to_string(),
            steam_id: "76561198000000000".to_string(),
            linked_at: "2024-01-01T00:00:00Z".to_string(),
        };
        let json = serde_json::to_string(&entry).unwrap();
        let deserialized: SteamIdEntry = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.steam_id, "76561198000000000");
        assert_eq!(deserialized.linked_at, "2024-01-01T00:00:00Z");
    }

    #[test]
    fn test_heartbeat_response_serde() {
        let resp = HeartbeatResponse {
            session_id: "sess_123".to_string(),
            status: "active".to_string(),
            heartbeat_count: 5,
            session_duration_secs: 300,
            validated: true,
        };
        let json = serde_json::to_string(&resp).unwrap();
        let deserialized: HeartbeatResponse = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.session_id, "sess_123");
        assert_eq!(deserialized.heartbeat_count, 5);
        assert!(deserialized.validated);
    }

    #[test]
    fn test_heartbeat_response_defaults() {
        // validated defaults to false when missing
        let json = r#"{"session_id":"s","status":"active","heartbeat_count":0,"session_duration_secs":0}"#;
        let resp: HeartbeatResponse = serde_json::from_str(json).unwrap();
        assert!(!resp.validated);
    }

    #[test]
    fn test_stop_session_response_serde() {
        let resp = StopSessionResponse { ok: true };
        let json = serde_json::to_string(&resp).unwrap();
        let deserialized: StopSessionResponse = serde_json::from_str(&json).unwrap();
        assert!(deserialized.ok);
    }

    #[test]
    fn test_leaderboard_entry_serde() {
        let entry = LeaderboardEntry {
            rank: 1,
            user_id: 42,
            username: "topseeder".to_string(),
            discord_id: Some("123456789".to_string()),
            total_time_secs: 36000,
            session_count: 10,
        };
        let json = serde_json::to_string(&entry).unwrap();
        let deserialized: LeaderboardEntry = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.rank, 1);
        assert_eq!(deserialized.username, "topseeder");
        assert_eq!(deserialized.total_time_secs, 36000);
    }

    #[test]
    fn test_leaderboard_entry_no_discord() {
        let json = r#"{"rank":5,"user_id":99,"username":"anon","discord_id":null,"total_time_secs":100,"session_count":1}"#;
        let entry: LeaderboardEntry = serde_json::from_str(json).unwrap();
        assert_eq!(entry.discord_id, None);
    }

    #[test]
    fn test_server_breakdown_serde() {
        let sb = ServerBreakdown {
            server_name: "Esprit PF".to_string(),
            total_time_secs: 7200,
            session_count: 3,
        };
        let json = serde_json::to_string(&sb).unwrap();
        let deserialized: ServerBreakdown = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.server_name, "Esprit PF");
        assert_eq!(deserialized.total_time_secs, 7200);
    }

    #[test]
    fn test_recent_session_serde() {
        let session = RecentSession {
            session_id: "sess_abc".to_string(),
            server_name: "EU Server".to_string(),
            status: "completed".to_string(),
            started_at: 1700000000,
            ended_at: Some(1700003600),
            duration_secs: 3600,
        };
        let json = serde_json::to_string(&session).unwrap();
        let deserialized: RecentSession = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.session_id, "sess_abc");
        assert_eq!(deserialized.ended_at, Some(1700003600));
        assert_eq!(deserialized.duration_secs, 3600);
    }

    #[test]
    fn test_recent_session_no_end() {
        let json = r#"{"session_id":"s","server_name":"srv","status":"active","started_at":0,"ended_at":null,"duration_secs":120}"#;
        let session: RecentSession = serde_json::from_str(json).unwrap();
        assert_eq!(session.ended_at, None);
    }

    #[test]
    fn test_user_seeding_stats_serde() {
        let stats = UserSeedingStats {
            total_time_secs: 50000,
            session_count: 20,
            avg_session_secs: 2500,
            servers: vec![ServerBreakdown {
                server_name: "NA-1".to_string(),
                total_time_secs: 50000,
                session_count: 20,
            }],
            recent_sessions: vec![],
        };
        let json = serde_json::to_string(&stats).unwrap();
        let deserialized: UserSeedingStats = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.total_time_secs, 50000);
        assert_eq!(deserialized.servers.len(), 1);
        assert!(deserialized.recent_sessions.is_empty());
    }

    #[test]
    fn test_region_candidate_serde() {
        let candidate = RegionCandidate {
            game: "hll".to_string(),
            index: 0,
            server: ServerInfo {
                ip: "1.2.3.4".to_string(),
                bm_id: 100,
                short_name: "PF".to_string(),
                name: "Esprit PF".to_string(),
                seeding_threshold: 50,
                game: "hll".to_string(),
            },
        };
        let json = serde_json::to_string(&candidate).unwrap();
        let deserialized: RegionCandidate = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.game, "hll");
        assert_eq!(deserialized.index, 0);
        assert_eq!(deserialized.server.short_name, "PF");
    }

    #[test]
    fn test_game_seeding_status_serde() {
        let status = GameSeedingStatus {
            na: Some(RegionCandidate {
                game: "hll".to_string(),
                index: 0,
                server: ServerInfo {
                    ip: "1.2.3.4".to_string(),
                    bm_id: 100,
                    short_name: "NA-1".to_string(),
                    name: "NA Server".to_string(),
                    seeding_threshold: 50,
                    game: "hll".to_string(),
                },
            }),
            eu: None,
        };
        let json = serde_json::to_string(&status).unwrap();
        let deserialized: GameSeedingStatus = serde_json::from_str(&json).unwrap();
        assert!(deserialized.na.is_some());
        assert!(deserialized.eu.is_none());
    }

    #[test]
    fn test_seeding_status_response_serde() {
        let resp = SeedingStatusResponse {
            hll: GameSeedingStatus { na: None, eu: None },
            hllv: None,
            updated_at: 1700000000,
        };
        let json = serde_json::to_string(&resp).unwrap();
        let deserialized: SeedingStatusResponse = serde_json::from_str(&json).unwrap();
        assert!(deserialized.hll.na.is_none());
        assert!(deserialized.hllv.is_none());
        assert_eq!(deserialized.updated_at, 1700000000);
    }

    #[test]
    fn test_seeding_status_response_defaults() {
        // hllv defaults to None when missing
        let json = r#"{"hll":{"na":null,"eu":null},"updated_at":0}"#;
        let resp: SeedingStatusResponse = serde_json::from_str(json).unwrap();
        assert!(resp.hllv.is_none());
    }

    #[test]
    fn test_start_session_response_serde() {
        let resp = StartSessionResponse {
            session_id: "sess_xyz".to_string(),
        };
        let json = serde_json::to_string(&resp).unwrap();
        let deserialized: StartSessionResponse = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.session_id, "sess_xyz");
    }

    #[test]
    fn test_next_server_response_serde() {
        let resp = NextServerResponse {
            game: "hll".to_string(),
            region: "na".to_string(),
            index: 1,
            server: ServerInfo {
                ip: "5.6.7.8".to_string(),
                bm_id: 200,
                short_name: "GW".to_string(),
                name: "Esprit G&W".to_string(),
                seeding_threshold: 40,
                game: "hll".to_string(),
            },
            all_exhausted: false,
            session_id: Some("sess_123".to_string()),
        };
        let json = serde_json::to_string(&resp).unwrap();
        let deserialized: NextServerResponse = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.index, 1);
        assert_eq!(deserialized.server.short_name, "GW");
        assert!(!deserialized.all_exhausted);
        assert_eq!(deserialized.session_id, Some("sess_123".to_string()));
    }

    #[test]
    fn test_next_server_response_defaults() {
        let json = r#"{"game":"hll","region":"na","index":0,"server":{"ip":"1.2.3.4","bm_id":1,"short_name":"S","name":"S","seeding_threshold":50},"all_exhausted":true}"#;
        let resp: NextServerResponse = serde_json::from_str(json).unwrap();
        assert_eq!(resp.session_id, None);
        assert!(resp.all_exhausted);
    }

    #[test]
    fn test_region_servers_serde() {
        let rs = RegionServers {
            na: vec![ServerInfo {
                ip: "1.2.3.4".to_string(),
                bm_id: 1,
                short_name: "NA".to_string(),
                name: "NA Server".to_string(),
                seeding_threshold: 50,
                game: "hll".to_string(),
            }],
            eu: vec![],
        };
        let json = serde_json::to_string(&rs).unwrap();
        let deserialized: RegionServers = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.na.len(), 1);
        assert!(deserialized.eu.is_empty());
    }

    #[test]
    fn test_servers_response_serde() {
        let resp = ServersResponse {
            hll: RegionServers { na: vec![], eu: vec![] },
            hllv: None,
            cached_at: 1700000000,
        };
        let json = serde_json::to_string(&resp).unwrap();
        let deserialized: ServersResponse = serde_json::from_str(&json).unwrap();
        assert!(deserialized.hllv.is_none());
        assert_eq!(deserialized.cached_at, 1700000000);
    }

    #[test]
    fn test_register_response_serde() {
        let resp = RegisterResponse {
            user_id: "42".to_string(),
            api_key: "key_abc123".to_string(),
            username: "guest_12345".to_string(),
            display_name: "Guest Player".to_string(),
        };
        let json = serde_json::to_string(&resp).unwrap();
        let deserialized: RegisterResponse = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.user_id, "42");
        assert_eq!(deserialized.api_key, "key_abc123");
        assert_eq!(deserialized.username, "guest_12345");
        assert_eq!(deserialized.display_name, "Guest Player");
    }

    #[test]
    fn test_linked_provider_serde() {
        let lp = LinkedProvider {
            provider: AuthProvider::Steam,
            provider_id: "76561198000000000".to_string(),
            display_name: Some("SteamUser".to_string()),
            linked_at: 1700000000,
        };
        let json = serde_json::to_string(&lp).unwrap();
        let deserialized: LinkedProvider = serde_json::from_str(&json).unwrap();
        assert_eq!(deserialized.provider, AuthProvider::Steam);
        assert_eq!(deserialized.provider_id, "76561198000000000");
        assert_eq!(deserialized.display_name, Some("SteamUser".to_string()));
        assert_eq!(deserialized.linked_at, 1700000000);
    }

    #[test]
    fn test_linked_provider_no_display_name() {
        let json = r#"{"provider":"discord","provider_id":"123456789","linked_at":0}"#;
        let lp: LinkedProvider = serde_json::from_str(json).unwrap();
        assert_eq!(lp.provider, AuthProvider::Discord);
        assert_eq!(lp.display_name, None);
    }
}

/// Response from POST /api/auth/register (guest registration)
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RegisterResponse {
    pub user_id: String,
    pub api_key: String,
    pub username: String,
    pub display_name: String,
}

/// Linked provider info
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LinkedProvider {
    pub provider: AuthProvider,
    pub provider_id: String,
    #[serde(default)]
    pub display_name: Option<String>,
    pub linked_at: u64,
}
