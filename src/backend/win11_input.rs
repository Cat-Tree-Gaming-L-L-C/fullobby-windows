//! Alternative input methods for Windows 11 locked screen scenarios.
//!
//! Windows 11 tightened restrictions on PostMessage to background windows when
//! the screen is locked. This module provides alternative approaches:
//!
//! 1. UI Automation - Microsoft's accessibility framework, may have different restrictions
//! 2. SendInput - Hardware-level input injection, different code path than PostMessage

use windows::Win32::{
    Foundation::HWND,
    System::Com::{CoCreateInstance, CoInitializeEx, CoUninitialize, CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED},
    UI::{
        Accessibility::{CUIAutomation, IUIAutomation, IUIAutomationElement},
        Input::KeyboardAndMouse::{
            SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_KEYUP,
            VK_ESCAPE, VK_F13,
        },
    },
};

use crate::backend::window_focus::find_hll_hwnd;

/// Check if we're running on Windows 11 or later.
/// Windows 11 is version 10.0.22000 or higher.
pub fn is_windows_11_or_later() -> bool {
    use windows::Win32::System::SystemInformation::{GetVersionExW, OSVERSIONINFOW};

    let mut version_info: OSVERSIONINFOW = unsafe { std::mem::zeroed() };
    version_info.dwOSVersionInfoSize = std::mem::size_of::<OSVERSIONINFOW>() as u32;

    unsafe {
        // GetVersionExW is deprecated but still works for basic version detection
        // For build number, we'd need RtlGetVersion or registry lookup
        if GetVersionExW(&mut version_info).is_ok() {
            // Windows 11 is 10.0.22000+
            // Major version 10, we need to check build number
            if version_info.dwMajorVersion >= 10 {
                // Build number 22000+ is Windows 11
                return version_info.dwBuildNumber >= 22000;
            }
        }
    }
    false
}

/// Send F13 key using SendInput API (hardware-level input injection).
/// This is different from PostMessage as it goes through the hardware input queue.
///
/// Returns Ok(true) if input was sent, Ok(false) if it failed.
pub fn send_f13_via_sendinput() -> Result<bool, String> {
    // Create key down event
    let mut inputs: [INPUT; 2] = unsafe { std::mem::zeroed() };

    // F13 key down
    inputs[0].r#type = INPUT_KEYBOARD;
    inputs[0].Anonymous = INPUT_0 {
        ki: KEYBDINPUT {
            wVk: VK_F13,
            wScan: 0,
            dwFlags: Default::default(),
            time: 0,
            dwExtraInfo: 0,
        },
    };

    // F13 key up
    inputs[1].r#type = INPUT_KEYBOARD;
    inputs[1].Anonymous = INPUT_0 {
        ki: KEYBDINPUT {
            wVk: VK_F13,
            wScan: 0,
            dwFlags: KEYEVENTF_KEYUP,
            time: 0,
            dwExtraInfo: 0,
        },
    };

    let sent = unsafe {
        SendInput(&inputs, std::mem::size_of::<INPUT>() as i32)
    };

    if sent == 2 {
        Ok(true)
    } else {
        Ok(false)
    }
}

/// Send Escape key using SendInput API.
pub fn send_escape_via_sendinput() -> Result<bool, String> {
    let mut inputs: [INPUT; 2] = unsafe { std::mem::zeroed() };

    // Escape key down
    inputs[0].r#type = INPUT_KEYBOARD;
    inputs[0].Anonymous = INPUT_0 {
        ki: KEYBDINPUT {
            wVk: VK_ESCAPE,
            wScan: 0,
            dwFlags: Default::default(),
            time: 0,
            dwExtraInfo: 0,
        },
    };

    // Escape key up
    inputs[1].r#type = INPUT_KEYBOARD;
    inputs[1].Anonymous = INPUT_0 {
        ki: KEYBDINPUT {
            wVk: VK_ESCAPE,
            wScan: 0,
            dwFlags: KEYEVENTF_KEYUP,
            time: 0,
            dwExtraInfo: 0,
        },
    };

    let sent = unsafe {
        SendInput(&inputs, std::mem::size_of::<INPUT>() as i32)
    };

    if sent == 2 {
        Ok(true)
    } else {
        Ok(false)
    }
}

/// UI Automation based input - attempts to use Windows accessibility framework
/// to interact with the game window.
///
/// This is a different approach than PostMessage/SendInput and may work
/// in scenarios where those are blocked.
pub struct UIAutomationInput {
    automation: IUIAutomation,
}

impl UIAutomationInput {
    /// Create a new UI Automation handler.
    /// Initializes COM and creates the automation instance.
    pub fn new() -> Result<Self, String> {
        unsafe {
            // Initialize COM (multithreaded)
            CoInitializeEx(None, COINIT_MULTITHREADED)
                .ok()
                .map_err(|e| format!("Failed to initialize COM: {:?}", e))?;

            // Create UI Automation instance
            let automation: IUIAutomation = CoCreateInstance(&CUIAutomation, None, CLSCTX_INPROC_SERVER)
                .map_err(|e| format!("Failed to create UI Automation: {:?}", e))?;

            Ok(Self { automation })
        }
    }

    /// Get the UI Automation element for the HLL window.
    pub fn get_hll_element(&self) -> Result<IUIAutomationElement, String> {
        let hwnd = find_hll_hwnd()
            .ok_or_else(|| "HLL window not found".to_string())?;

        unsafe {
            // Convert winapi HWND to windows crate HWND
            let hwnd_windows = HWND(hwnd as *mut std::ffi::c_void);

            self.automation
                .ElementFromHandle(hwnd_windows)
                .map_err(|e| format!("Failed to get element from HWND: {:?}", e))
        }
    }
}

impl Drop for UIAutomationInput {
    fn drop(&mut self) {
        unsafe { CoUninitialize(); }
    }
}

/// Combined approach: Try UI Automation focus + SendInput.
/// This is the main entry point for Win11 alternative input.
pub fn send_keys_via_uia(send_escape: bool) -> Result<bool, String> {
    let uia = UIAutomationInput::new()?;

    // Try to get the element and set focus
    let element = match uia.get_hll_element() {
        Ok(e) => e,
        Err(_) => return Ok(false), // Window not found
    };

    unsafe {
        // Attempt to set focus via UI Automation
        // This may work in scenarios where regular focus APIs fail
        let focus_result = element.SetFocus();
        if let Err(e) = &focus_result {
            log::debug!("UI Automation SetFocus returned: {:?}", e);
        }

        // Brief delay for focus to take effect
        std::thread::sleep(std::time::Duration::from_millis(50));
    }

    // Now send keys via SendInput
    if send_escape {
        send_escape_via_sendinput()?;
        std::thread::sleep(std::time::Duration::from_millis(50));
    }

    send_f13_via_sendinput()
}

/// Try all available methods to send input to HLL.
/// Attempts methods in order of likelihood to work on Win11 locked:
/// 1. UI Automation focus + SendInput
/// 2. Direct SendInput (may work if game checks hardware queue)
pub fn try_all_input_methods(send_escape: bool) -> Result<bool, String> {
    // First try UI Automation approach
    match send_keys_via_uia(send_escape) {
        Ok(true) => {
            log::info!("UI Automation input method succeeded");
            return Ok(true);
        }
        Ok(false) => {
            log::debug!("UI Automation: window not found");
        }
        Err(e) => {
            log::warn!("UI Automation approach failed: {}", e);
        }
    }

    // Fallback to direct SendInput
    if send_escape {
        send_escape_via_sendinput()?;
        std::thread::sleep(std::time::Duration::from_millis(50));
    }

    match send_f13_via_sendinput() {
        Ok(true) => {
            log::info!("Direct SendInput succeeded");
            Ok(true)
        }
        Ok(false) => {
            log::warn!("SendInput failed to send keys");
            Ok(false)
        }
        Err(e) => {
            log::error!("SendInput error: {}", e);
            Err(e)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_is_windows_11_detection() {
        // Just ensure it doesn't panic
        let _ = is_windows_11_or_later();
    }
}
