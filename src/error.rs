use std::fmt;

/// Application error type that replaces `tauri::ipc::InvokeError`.
/// Used throughout the backend for command return types.
#[derive(Debug, Clone)]
pub struct AppError {
    pub message: String,
}

impl AppError {
    pub fn new(msg: impl Into<String>) -> Self {
        Self {
            message: msg.into(),
        }
    }
}

impl fmt::Display for AppError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.message)
    }
}

impl std::error::Error for AppError {}

impl From<String> for AppError {
    fn from(s: String) -> Self {
        Self::new(s)
    }
}

impl From<&str> for AppError {
    fn from(s: &str) -> Self {
        Self::new(s)
    }
}

impl From<std::io::Error> for AppError {
    fn from(e: std::io::Error) -> Self {
        Self::new(e.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_new_stores_message() {
        let err = AppError::new("something failed");
        assert_eq!(err.message, "something failed");
    }

    #[test]
    fn test_new_from_string() {
        let err = AppError::new(String::from("owned message"));
        assert_eq!(err.message, "owned message");
    }

    #[test]
    fn test_display() {
        let err = AppError::new("display test");
        assert_eq!(format!("{}", err), "display test");
    }

    #[test]
    fn test_from_string() {
        let err: AppError = String::from("from string").into();
        assert_eq!(err.message, "from string");
    }

    #[test]
    fn test_from_str() {
        let err: AppError = "from str".into();
        assert_eq!(err.message, "from str");
    }

    #[test]
    fn test_from_io_error() {
        let io_err = std::io::Error::new(std::io::ErrorKind::NotFound, "file missing");
        let err: AppError = io_err.into();
        assert!(err.message.contains("file missing"));
    }
}
