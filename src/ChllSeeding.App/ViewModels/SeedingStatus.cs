namespace ChllSeeding.App.ViewModels;

/// <summary>
/// UI-facing seeding lifecycle state. Mirrors the Rust <c>SeedingStatus</c> enum
/// (src-rust/src/state/seeding.rs) that drove the Dioxus seed/launch components.
/// </summary>
public enum SeedingStatus
{
    /// <summary>Nothing running; the seed/launch buttons are shown.</summary>
    Idle,

    /// <summary>Launch requested; waiting for the game to open and the splash bypass to run.</summary>
    Initializing,

    /// <summary>Actively seeding (game running, monitor loop active).</summary>
    Seeding,

    /// <summary>Game launched directly (Launch tab) without the seeding monitor.</summary>
    Running,

    /// <summary>Stop requested; tearing down.</summary>
    Stopping,

    /// <summary>Switching servers (game being killed before the next candidate).</summary>
    Switching,

    /// <summary>Seeding ended cleanly (user-stopped or game closed).</summary>
    Stopped,

    /// <summary>Game would not open (likely a Steam update); the launch watcher is retrying.</summary>
    WaitingForUpdate,
}
