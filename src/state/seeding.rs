use dioxus::prelude::*;

/// Seeding status — mirrors the TypeScript SeedingStatus type
#[derive(Debug, Clone, PartialEq, Default)]
pub enum SeedingStatus {
    #[default]
    Idle,
    Initializing,
    Seeding,
    Running,
    Stopping,
    Stopped,
    Switching,
    WaitingForUpdate,
    Error,
}

/// Seeding region — NA or EU
#[derive(Debug, Clone, PartialEq, Default)]
pub enum SeedingRegion {
    #[default]
    Na,
    Eu,
}

impl SeedingRegion {
    pub fn as_str(&self) -> &str {
        match self {
            SeedingRegion::Na => "na",
            SeedingRegion::Eu => "eu",
        }
    }
}

// Global seeding state
pub static SEEDING_STATUS: GlobalSignal<SeedingStatus> = Signal::global(SeedingStatus::default);
pub static SEEDING_INDEX: GlobalSignal<Option<usize>> = Signal::global(|| None);
pub static SEEDING_REGION: GlobalSignal<SeedingRegion> = Signal::global(SeedingRegion::default);
pub static IS_SEEDING: GlobalSignal<bool> = Signal::global(|| false);
/// Whether the current seed was initiated via "Seed All" (enables multi-region retry).
pub static IS_SEED_ALL: GlobalSignal<bool> = Signal::global(|| false);
/// Which game is currently being seeded ("hll", "hllv", etc.)
pub static SEEDING_GAME: GlobalSignal<Option<String>> = Signal::global(|| None);
/// Human-readable error message for the most recent seeding error
pub static SEEDING_ERROR_MESSAGE: GlobalSignal<String> = Signal::global(String::new);

// Server switch / snooze state
pub static SERVER_SWITCH_ACTIVE: GlobalSignal<bool> = Signal::global(|| false);
pub static SERVER_SWITCH_COUNTDOWN: GlobalSignal<u64> = Signal::global(|| 0);
pub static SERVER_SWITCH_SERVER_NAME: GlobalSignal<String> = Signal::global(String::new);
pub static SERVER_SWITCH_REASON: GlobalSignal<String> = Signal::global(String::new);
pub static SERVER_SWITCH_SNOOZED: GlobalSignal<bool> = Signal::global(|| false);
pub static SERVER_SWITCH_SNOOZE_REMAINING: GlobalSignal<u64> = Signal::global(|| 0);

pub fn reset_server_switch() {
    *SERVER_SWITCH_ACTIVE.write() = false;
    *SERVER_SWITCH_COUNTDOWN.write() = 0;
    *SERVER_SWITCH_SERVER_NAME.write() = String::new();
    *SERVER_SWITCH_REASON.write() = String::new();
    *SERVER_SWITCH_SNOOZED.write() = false;
    *SERVER_SWITCH_SNOOZE_REMAINING.write() = 0;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_seeding_status_default_is_idle() {
        assert_eq!(SeedingStatus::default(), SeedingStatus::Idle);
    }

    #[test]
    fn test_seeding_status_equality() {
        assert_eq!(SeedingStatus::Seeding, SeedingStatus::Seeding);
        assert_ne!(SeedingStatus::Idle, SeedingStatus::Running);
        assert_ne!(SeedingStatus::Initializing, SeedingStatus::Switching);
    }

    #[test]
    fn test_seeding_status_clone() {
        let status = SeedingStatus::WaitingForUpdate;
        let cloned = status.clone();
        assert_eq!(status, cloned);
    }

    #[test]
    fn test_seeding_status_debug() {
        assert_eq!(format!("{:?}", SeedingStatus::Idle), "Idle");
        assert_eq!(format!("{:?}", SeedingStatus::Error), "Error");
        assert_eq!(format!("{:?}", SeedingStatus::Stopping), "Stopping");
    }

    #[test]
    fn test_seeding_status_all_variants_distinct() {
        let variants = vec![
            SeedingStatus::Idle,
            SeedingStatus::Initializing,
            SeedingStatus::Seeding,
            SeedingStatus::Running,
            SeedingStatus::Stopping,
            SeedingStatus::Stopped,
            SeedingStatus::Switching,
            SeedingStatus::WaitingForUpdate,
            SeedingStatus::Error,
        ];
        for (i, a) in variants.iter().enumerate() {
            for (j, b) in variants.iter().enumerate() {
                if i == j {
                    assert_eq!(a, b);
                } else {
                    assert_ne!(a, b);
                }
            }
        }
    }

    #[test]
    fn test_seeding_region_default_is_na() {
        assert_eq!(SeedingRegion::default(), SeedingRegion::Na);
    }

    #[test]
    fn test_seeding_region_as_str() {
        assert_eq!(SeedingRegion::Na.as_str(), "na");
        assert_eq!(SeedingRegion::Eu.as_str(), "eu");
    }

    #[test]
    fn test_seeding_region_equality() {
        assert_eq!(SeedingRegion::Na, SeedingRegion::Na);
        assert_eq!(SeedingRegion::Eu, SeedingRegion::Eu);
        assert_ne!(SeedingRegion::Na, SeedingRegion::Eu);
    }

    #[test]
    fn test_seeding_region_clone() {
        let region = SeedingRegion::Eu;
        let cloned = region.clone();
        assert_eq!(region, cloned);
        assert_eq!(cloned.as_str(), "eu");
    }

    #[test]
    fn test_seeding_region_debug() {
        assert_eq!(format!("{:?}", SeedingRegion::Na), "Na");
        assert_eq!(format!("{:?}", SeedingRegion::Eu), "Eu");
    }
}
