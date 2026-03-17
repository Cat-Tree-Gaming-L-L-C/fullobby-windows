pub fn get_default_config_path() -> Result<String, String> {
    dirs::data_local_dir()
        .ok_or_else(|| "Failed to locate local data directory".to_string())
        .map(|p| {
            p.join("HLL")
                .join("Saved")
                .join("Config")
                .join("WindowsNoEditor")
                .to_string_lossy()
                .to_string()
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_get_default_config_path_succeeds() {
        let result = get_default_config_path();
        assert!(result.is_ok(), "get_default_config_path should succeed on Windows");
    }

    #[test]
    fn test_get_default_config_path_contains_expected_components() {
        let path = get_default_config_path().unwrap();
        assert!(path.contains("HLL"), "Path should contain 'HLL': {}", path);
        assert!(path.contains("Saved"), "Path should contain 'Saved': {}", path);
        assert!(path.contains("Config"), "Path should contain 'Config': {}", path);
        assert!(path.contains("WindowsNoEditor"), "Path should contain 'WindowsNoEditor': {}", path);
    }

    #[test]
    fn test_get_default_config_path_is_absolute() {
        let path = get_default_config_path().unwrap();
        // On Windows, absolute paths start with a drive letter (e.g., C:\)
        assert!(
            path.chars().nth(1) == Some(':'),
            "Path should be absolute (start with drive letter): {}",
            path
        );
    }
}
