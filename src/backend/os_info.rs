use windows::Win32::System::SystemInformation::{GetVersionExW, OSVERSIONINFOW};

/// Get the Windows version string (e.g. "10.0.22631").
pub fn os_version() -> String {
    let mut info: OSVERSIONINFOW = unsafe { std::mem::zeroed() };
    info.dwOSVersionInfoSize = std::mem::size_of::<OSVERSIONINFOW>() as u32;

    unsafe {
        if GetVersionExW(&mut info).is_ok() {
            return format!(
                "{}.{}.{}",
                info.dwMajorVersion, info.dwMinorVersion, info.dwBuildNumber
            );
        }
    }
    "unknown".to_string()
}

/// Get the CPU architecture (e.g. "x86_64", "aarch64").
pub fn os_arch() -> &'static str {
    std::env::consts::ARCH
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_os_version_not_empty() {
        let v = os_version();
        assert!(!v.is_empty());
        // Should either be "unknown" or a version like "10.0.XXXXX"
        assert!(v == "unknown" || v.contains('.'));
    }

    #[test]
    fn test_os_arch_not_empty() {
        let a = os_arch();
        assert!(!a.is_empty());
    }
}
