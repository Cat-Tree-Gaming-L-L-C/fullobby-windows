# Esprit Seeder Windows

Dioxus 0.7 desktop app (Rust) for Hell Let Loose server seeding.

## Build Commands

- `dx serve` — dev build with hot reload
- `dx build --release` — release build (exe only)
- `dx bundle --release` — build release installers (MSI + NSIS), then launch with:
  `start "" "target/dx/esprit-seeder/bundle/windows/bundle/nsis/EspritSeeder_0.1.0_x64-setup.exe"`
