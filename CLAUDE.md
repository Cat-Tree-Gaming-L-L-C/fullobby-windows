# CHLL Seeding Windows

C# + WinUI 3 (Windows App SDK) desktop app for Hell Let Loose server seeding.
Formerly "Esprit Seeder" (Rust/Dioxus); rewritten to native C#/WinUI 3.

**Read `docs/ARCHITECTURE.md` first** — solution layout, Rust→C# subsystem map,
WinUI 3 gotchas, and the open cross-repo coordination items.

## Layout

- `src/` — C# solution (`ChllSeeding.sln`: App, Core, Core.Tests)
- `installer/` — Inno Setup script
- `tools/ChllSeeding.MockApi/` — local mock API for offline dev
- `docs/ARCHITECTURE.md` — architecture reference

## Build Commands (Windows, dotnet CLI)

- `dotnet build src/ChllSeeding.sln -c Release` — build
- `dotnet test src/ChllSeeding.Core.Tests` — run tests
- App is **unpackaged** WinUI 3 (no MSIX); installer is built with Inno Setup.

## Key facts

- Target: .NET 9, Windows App SDK 1.8.x, min Windows 10.0.19041
- Deep-link protocol: `chllseeding://` (OAuth callbacks)
- API base: `https://seeding-api.comp-hll.org` (configurable, TBD)
- Clean break from Esprit-branded identifiers — no config migration
