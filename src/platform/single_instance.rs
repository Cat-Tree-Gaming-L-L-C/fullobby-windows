use std::ptr;
use tracing::{info, warn, error};
use winapi::um::synchapi::CreateMutexW;
use winapi::um::errhandlingapi::GetLastError;
use winapi::um::handleapi::CloseHandle;

const ERROR_ALREADY_EXISTS: u32 = 183;

/// Named pipe path for IPC between instances
const PIPE_NAME: &str = r"\\.\pipe\HLLSeederSingleInstance";

/// Maximum bytes to read from a single pipe connection to prevent
/// memory exhaustion from a malicious local process.
const MAX_PAYLOAD_BYTES: u64 = 8192;

/// Holds a Windows security descriptor allocated by `ConvertStringSecurityDescriptorToSecurityDescriptorW`.
/// Automatically freed with `LocalFree` on drop.
struct PipeSecurityDescriptor {
    sa: winapi::um::minwinbase::SECURITY_ATTRIBUTES,
    /// Raw pointer to the SD allocation — must be freed with LocalFree.
    _sd: *mut winapi::ctypes::c_void,
}

// SAFETY: The security descriptor is a self-contained heap allocation from Windows
// that is only read after construction and freed on drop. It is not shared across threads.
unsafe impl Send for PipeSecurityDescriptor {}

impl Drop for PipeSecurityDescriptor {
    fn drop(&mut self) {
        if !self._sd.is_null() {
            unsafe {
                winapi::um::winbase::LocalFree(self._sd);
            }
        }
    }
}

/// Build a SECURITY_ATTRIBUTES that restricts the named pipe to the current user only.
/// Returns None if Windows API calls fail (caller should fall back to default ACL with a warning).
fn build_pipe_security() -> Option<PipeSecurityDescriptor> {
    use winapi::um::processthreadsapi::{GetCurrentProcess, OpenProcessToken};
    use winapi::um::securitybaseapi::GetTokenInformation;
    use winapi::um::winnt::{TokenUser, TOKEN_QUERY, TOKEN_USER};
    use winapi::shared::sddl::{ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW};

    unsafe {
        // Open current process token
        let mut token = ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return None;
        }

        // Query TOKEN_USER size, then retrieve it
        let mut len: u32 = 0;
        GetTokenInformation(token, TokenUser, ptr::null_mut(), 0, &mut len);
        let mut buf = vec![0u8; len as usize];
        if GetTokenInformation(token, TokenUser, buf.as_mut_ptr() as *mut _, len, &mut len) == 0 {
            CloseHandle(token);
            return None;
        }
        CloseHandle(token);

        let token_user = &*(buf.as_ptr() as *const TOKEN_USER);

        // Convert user SID to string form (e.g. "S-1-5-21-...")
        let mut sid_ptr: *mut u16 = ptr::null_mut();
        if ConvertSidToStringSidW(token_user.User.Sid, &mut sid_ptr) == 0 {
            return None;
        }
        let sid_len = (0..).take_while(|&i| *sid_ptr.add(i) != 0).count();
        let sid_string = String::from_utf16_lossy(std::slice::from_raw_parts(sid_ptr, sid_len));
        winapi::um::winbase::LocalFree(sid_ptr as *mut _);

        // Build SDDL: Protected DACL granting Generic All to the current user only.
        // D:P = protected DACL (no inheritance), (A;;GA;;;SID) = allow Generic All.
        let sddl = format!("D:P(A;;GA;;;{})", sid_string);
        let sddl_wide: Vec<u16> = sddl.encode_utf16().chain(std::iter::once(0)).collect();

        // Convert SDDL to a binary security descriptor
        let mut sd: *mut winapi::ctypes::c_void = ptr::null_mut();
        if ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl_wide.as_ptr(),
            1, // SDDL_REVISION_1
            &mut sd,
            ptr::null_mut(),
        ) == 0 {
            return None;
        }

        let mut sa: winapi::um::minwinbase::SECURITY_ATTRIBUTES = std::mem::zeroed();
        sa.nLength = std::mem::size_of::<winapi::um::minwinbase::SECURITY_ATTRIBUTES>() as u32;
        sa.lpSecurityDescriptor = sd;
        sa.bInheritHandle = 0;

        Some(PipeSecurityDescriptor { sa, _sd: sd })
    }
}

/// Windows named mutex for single instance detection.
/// Returns true if this is the first instance, false if another instance is running.
pub fn acquire_single_instance(name: &str) -> bool {
    let wide_name: Vec<u16> = name.encode_utf16().chain(std::iter::once(0)).collect();

    unsafe {
        let handle = CreateMutexW(ptr::null_mut(), 0, wide_name.as_ptr());

        if handle.is_null() {
            warn!("Failed to create named mutex");
            return true; // Allow running if we can't check
        }

        let error = GetLastError();
        if error == ERROR_ALREADY_EXISTS {
            CloseHandle(handle);
            info!("Another instance is already running");
            return false;
        }

        // Keep the mutex handle alive for the lifetime of the process
        // (intentionally leaked — OS will clean up on process exit)
        let _ = handle;
        info!("Single instance lock acquired");
        true
    }
}

/// Send CLI args to the already-running instance via named pipe.
/// Called by the second instance before it exits.
pub fn send_args_to_running_instance(args: &[String]) {
    use std::io::Write;
    use std::fs::OpenOptions;

    let payload = args.join("\n");
    info!("Sending args to running instance via named pipe: {:?}", args);

    match OpenOptions::new().write(true).open(PIPE_NAME) {
        Ok(mut pipe) => {
            if let Err(e) = pipe.write_all(payload.as_bytes()) {
                error!("Failed to write to named pipe: {}", e);
            } else {
                info!("Args sent to running instance successfully");
            }
        }
        Err(e) => {
            error!("Failed to connect to named pipe: {}", e);
        }
    }
}

/// Start a background thread that listens for args from new instances via named pipe.
/// When args are received, fires `AppEvent::SingleInstance { args }`.
pub fn start_single_instance_listener() {
    std::thread::spawn(|| {
        info!("Single instance named pipe listener started");
        loop {
            match listen_once() {
                Ok(args) => {
                    if !args.is_empty() {
                        info!("Received args from another instance: {:?}", args);
                        crate::events::send_event(crate::events::AppEvent::SingleInstance { args });
                    }
                }
                Err(e) => {
                    warn!("Named pipe listener error: {}", e);
                    // Brief sleep before retrying to avoid busy loop on persistent errors
                    std::thread::sleep(std::time::Duration::from_secs(1));
                }
            }
        }
    });
}

/// Parse a newline-delimited payload into args (shared logic for testing).
fn parse_pipe_payload(buf: &str) -> Vec<String> {
    buf.lines().map(|s| s.to_string()).filter(|s| !s.is_empty()).collect()
}

/// Create a named pipe, wait for one connection, read the payload, return parsed args.
fn listen_once() -> Result<Vec<String>, Box<dyn std::error::Error>> {
    use std::io::Read;
    use winapi::um::winbase::CreateNamedPipeA;
    use winapi::um::namedpipeapi::ConnectNamedPipe;
    use winapi::um::handleapi::CloseHandle;
    use winapi::um::winbase::{PIPE_ACCESS_INBOUND, PIPE_TYPE_BYTE, PIPE_WAIT};

    let pipe_name_cstr: Vec<u8> = PIPE_NAME.bytes().chain(std::iter::once(0)).collect();

    // Restrict pipe to current user via ACL (prevents other local users from sending commands)
    let mut security = build_pipe_security();
    if security.is_none() {
        warn!("Failed to build pipe security descriptor — pipe will use default ACL");
    }
    let sa_ptr = match security.as_mut() {
        Some(ctx) => &mut ctx.sa as *mut _,
        None => ptr::null_mut(),
    };

    unsafe {
        let pipe_handle = CreateNamedPipeA(
            pipe_name_cstr.as_ptr() as *const i8,
            PIPE_ACCESS_INBOUND,
            PIPE_TYPE_BYTE | PIPE_WAIT,
            1,    // max instances
            4096, // out buffer
            4096, // in buffer
            0,    // default timeout
            sa_ptr,
        );

        if pipe_handle == winapi::um::handleapi::INVALID_HANDLE_VALUE {
            return Err("Failed to create named pipe".into());
        }

        // Block until a client connects
        let connected = ConnectNamedPipe(pipe_handle, ptr::null_mut());
        if connected == 0 {
            let err = GetLastError();
            // ERROR_PIPE_CONNECTED (535) means client connected before we called ConnectNamedPipe — that's fine
            if err != 535 {
                CloseHandle(pipe_handle);
                return Err(format!("ConnectNamedPipe failed: error {}", err).into());
            }
        }

        // Read payload using std File from raw handle
        use std::os::windows::io::FromRawHandle;
        let file = std::fs::File::from_raw_handle(pipe_handle as *mut _);
        let mut buf = String::new();
        let _ = file.take(MAX_PAYLOAD_BYTES).read_to_string(&mut buf);
        // take() limits read size; dropping the Take wrapper closes the handle

        let args = parse_pipe_payload(&buf);
        Ok(args)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_pipe_payload_empty() {
        let args = parse_pipe_payload("");
        assert!(args.is_empty());
    }

    #[test]
    fn test_parse_pipe_payload_single_arg() {
        let args = parse_pipe_payload("--autoseed-na");
        assert_eq!(args, vec!["--autoseed-na"]);
    }

    #[test]
    fn test_parse_pipe_payload_multiple_args() {
        let args = parse_pipe_payload("--autoseed-na\n--verbose\n--debug");
        assert_eq!(args, vec!["--autoseed-na", "--verbose", "--debug"]);
    }

    #[test]
    fn test_parse_pipe_payload_filters_empty_lines() {
        let args = parse_pipe_payload("\n--autoseed-eu\n\n--flag\n\n");
        assert_eq!(args, vec!["--autoseed-eu", "--flag"]);
    }

    #[test]
    fn test_parse_pipe_payload_deep_link() {
        let args = parse_pipe_payload("espritseeder://auth/callback?token=abc&refresh_token=xyz");
        assert_eq!(args.len(), 1);
        assert!(args[0].starts_with("espritseeder://"));
    }

    #[test]
    fn test_parse_pipe_payload_windows_line_endings() {
        // Windows \r\n line endings
        let args = parse_pipe_payload("--autoseed-na\r\n--autoseed-eu");
        assert_eq!(args.len(), 2);
    }

    #[test]
    fn test_pipe_name_constant() {
        assert_eq!(PIPE_NAME, r"\\.\pipe\HLLSeederSingleInstance");
    }

    #[test]
    fn test_error_already_exists_constant() {
        assert_eq!(ERROR_ALREADY_EXISTS, 183);
    }

    #[test]
    fn test_acquire_single_instance_with_unique_name() {
        // Use a unique name so we don't conflict with the running app
        let unique_name = format!("TestMutex_{}", std::process::id());
        let result = acquire_single_instance(&unique_name);
        // Should succeed since no other instance has this mutex
        assert!(result);
    }
}
