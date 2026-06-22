# CHLL Seeding Windows

C# + WinUI 3 (Windows App SDK) desktop app for Hell Let Loose server seeding.
Formerly "Esprit Seeder" (Rust/Dioxus) — currently being rewritten and rebranded.

**Read `docs/REWRITE_PLAN.md` first** — it contains the full architecture, phase
breakdown, rebrand checklist, and current status of the rewrite.

## Layout

- `src/` — C# solution (`ChllSeeding.sln`: App, Core, Core.Tests) *(created in Phase 0)*
- `installer/` — Inno Setup script
- `docs/REWRITE_PLAN.md` — the rewrite plan

## Build Commands (Windows, dotnet CLI)

- `dotnet build src/ChllSeeding.sln -c Release` — build
- `dotnet test src/ChllSeeding.Core.Tests` — run tests
- App is **unpackaged** WinUI 3 (no MSIX); installer is built with Inno Setup.

## Key facts

- Target: .NET 9, Windows App SDK 1.8.x, min Windows 10.0.19041
- Deep-link protocol: `chllseeding://` (OAuth callbacks)
- API base: `https://seeding-api.comp-hll.org` (configurable, TBD)
- Clean break from Esprit-branded identifiers — no config migration
