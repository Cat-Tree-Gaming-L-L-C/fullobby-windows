use serde::{Deserialize, Serialize, Deserializer};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[allow(dead_code)] // Fields kept for deserialization but not read directly
pub struct ApiResponse {
    pub result: ApiResult,
    #[serde(skip_serializing)]
    pub command: String,
    #[serde(skip_serializing)]
    pub arguments: Option<serde_json::Value>,
    pub failed: bool,
    pub error: Option<String>,
    #[serde(skip_serializing)]
    pub forwards_results: Option<serde_json::Value>,
    #[serde(skip_serializing)]
    pub version: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[allow(dead_code)]
pub struct ApiResult {
    pub current_map: CurrentMap,
    pub next_map: NextMap,
    #[serde(deserialize_with = "float_to_u32")]
    pub player_count: u32,
    #[serde(deserialize_with = "float_to_u32")]
    pub max_player_count: u32,
    pub player_count_by_team: PlayerCountByTeam,
    pub score: Score,
    #[serde(deserialize_with = "float_to_u32")]
    pub time_remaining: u32,
    #[serde(skip_serializing)]
    pub vote_status: Vec<VoteStatus>,
    #[serde(skip_serializing)]
    pub name: ServerName,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CurrentMap {
    pub map: MapInfo,
    pub start: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct NextMap {
    pub map: MapInfo,
    pub start: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MapInfo {
    pub id: String,
    pub map: Map,
    pub game_mode: String,
    pub attackers: Option<String>,
    pub environment: String,
    pub pretty_name: String,
    pub image_name: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Map {
    pub id: String,
    pub name: String,
    pub tag: String,
    pub pretty_name: String,
    pub shortname: String,
    pub allies: Team,
    pub axis: Team,
    pub orientation: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Team {
    pub name: String,
    pub team: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PlayerCountByTeam {
    #[serde(deserialize_with = "float_to_u32")]
    pub allied: u32,
    #[serde(deserialize_with = "float_to_u32")]
    pub axis: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Score {
    #[serde(deserialize_with = "float_to_u32")]
    pub allied: u32,
    #[serde(deserialize_with = "float_to_u32")]
    pub axis: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VoteStatus {
    pub map: MapInfo,
    pub voters: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ServerName {
    pub name: String,
    pub short_name: String,
    #[serde(deserialize_with = "float_to_u32")]
    pub public_stats_port: u32,
    #[serde(deserialize_with = "float_to_u32")]
    pub public_stats_port_https: u32,
}

fn float_to_u32<'de, D>(deserializer: D) -> Result<u32, D::Error>
where
    D: Deserializer<'de>,
{
    let float = f64::deserialize(deserializer)?;
    // Security: Validate bounds before casting to prevent silent overflow/wraparound
    if float < 0.0 || float > u32::MAX as f64 {
        return Err(serde::de::Error::custom(
            format!("value {} out of range for u32", float)
        ));
    }
    Ok(float as u32)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde::Deserialize;
    use serde_json::json;

    #[derive(Debug, Deserialize, PartialEq)]
    struct TestStruct {
        #[serde(deserialize_with = "float_to_u32")]
        value: u32,
    }

    #[test]
    fn test_float_to_u32_integer_value() {
        let json = json!({"value": 42.0});
        let result: TestStruct = serde_json::from_value(json).unwrap();
        assert_eq!(result.value, 42);
    }

    #[test]
    fn test_float_to_u32_zero() {
        let json = json!({"value": 0.0});
        let result: TestStruct = serde_json::from_value(json).unwrap();
        assert_eq!(result.value, 0);
    }

    #[test]
    fn test_float_to_u32_large_value() {
        let json = json!({"value": 1000000.0});
        let result: TestStruct = serde_json::from_value(json).unwrap();
        assert_eq!(result.value, 1000000);
    }

    #[test]
    fn test_float_to_u32_max_value() {
        let json = json!({"value": 4294967295.0}); // u32::MAX
        let result: TestStruct = serde_json::from_value(json).unwrap();
        assert_eq!(result.value, u32::MAX);
    }

    #[test]
    fn test_float_to_u32_truncates_decimal() {
        let json = json!({"value": 42.9});
        let result: TestStruct = serde_json::from_value(json).unwrap();
        assert_eq!(result.value, 42);
    }

    #[test]
    fn test_float_to_u32_negative_fails() {
        let json = json!({"value": -1.0});
        let result: Result<TestStruct, _> = serde_json::from_value(json);
        assert!(result.is_err());
        let err = result.unwrap_err().to_string();
        assert!(err.contains("out of range for u32"));
    }

    #[test]
    fn test_float_to_u32_overflow_fails() {
        let json = json!({"value": 4294967296.0}); // u32::MAX + 1
        let result: Result<TestStruct, _> = serde_json::from_value(json);
        assert!(result.is_err());
        let err = result.unwrap_err().to_string();
        assert!(err.contains("out of range for u32"));
    }

    #[test]
    fn test_float_to_u32_large_overflow_fails() {
        let json = json!({"value": 9999999999999.0});
        let result: Result<TestStruct, _> = serde_json::from_value(json);
        assert!(result.is_err());
    }

    #[test]
    fn test_player_count_by_team_deserialization() {
        let json = json!({
            "allied": 45.0,
            "axis": 48.0
        });
        let result: PlayerCountByTeam = serde_json::from_value(json).unwrap();
        assert_eq!(result.allied, 45);
        assert_eq!(result.axis, 48);
    }

    #[test]
    fn test_score_deserialization() {
        let json = json!({
            "allied": 3.0,
            "axis": 2.0
        });
        let result: Score = serde_json::from_value(json).unwrap();
        assert_eq!(result.allied, 3);
        assert_eq!(result.axis, 2);
    }

    #[test]
    fn test_server_name_deserialization() {
        let json = json!({
            "name": "Test Server",
            "short_name": "TS",
            "public_stats_port": 8080.0,
            "public_stats_port_https": 8443.0
        });
        let result: ServerName = serde_json::from_value(json).unwrap();
        assert_eq!(result.name, "Test Server");
        assert_eq!(result.short_name, "TS");
        assert_eq!(result.public_stats_port, 8080);
        assert_eq!(result.public_stats_port_https, 8443);
    }
}
