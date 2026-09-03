# Fullobby Windows

C# + WinUI 3 (Windows App SDK) desktop app for Hell Let Loose server seeding.

**Read `docs/ARCHITECTURE.md` first** — solution layout, subsystem map,
WinUI 3 gotchas, and the open cross-repo coordination items.

## Layout

- `src/` — C# solution (`Fullobby.sln`: App, Core, Core.Tests)
- `installer/` — Inno Setup script
- `tools/Fullobby.MockApi/` — local mock API for offline dev
- `docs/ARCHITECTURE.md` — architecture reference

## Build Commands (Windows, dotnet CLI)

- `dotnet build src/Fullobby.sln -c Release` — build
- `dotnet test src/Fullobby.Core.Tests` — run tests
- App is **unpackaged** WinUI 3 (no MSIX); installer is built with Inno Setup.

## Key facts

- Target: .NET 9, Windows App SDK 1.8.x, min Windows 10.0.19041 (Win10 2004 / Win11)
- Deep-link protocol: `fullobby://` (OAuth callbacks)
- API base: `https://api.fullobby.com` (hardbaked in Release; `FULLOBBY_API_URL` overrides in Debug only)
- No legacy config migration — app identifiers are fresh throughout
