# Contributing to Fullobby

Thanks for your interest in improving the Fullobby client! This is a
community project by [Comp HLL](https://github.com/Cat-Tree-Gaming-L-L-C), and
contributions — bug reports, fixes, features, and docs — are welcome.

By participating you agree to abide by our
[Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- **Open an issue first** for anything non-trivial so we can agree on the
  approach before you spend time on a PR. Small fixes (typos, obvious bugs) can
  go straight to a PR.
- **Security issues are different.** Do **not** open a public issue for anything
  exploitable — follow [SECURITY.md](SECURITY.md) to report privately.
- Check [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the solution layout,
  subsystem map, and WinUI 3 gotchas.

## Development setup

**Requirements** (see the [README](README.md#prerequisites) for links):

- .NET 9 SDK
- Windows 10 (19041) or later — the app is Windows-only (WinUI 3 / Windows App SDK)
- Inno Setup 6 (only if you build the installer)

```powershell
git clone https://github.com/Cat-Tree-Gaming-L-L-C/fullobby-windows.git
cd fullobby-windows

dotnet build src/Fullobby.sln -c Release
dotnet test src/Fullobby.Core.Tests
```

For offline development without the live API, use the local mock in
`tools/Fullobby.MockApi/` (`scripts/run-mock.ps1`).

## Making changes

- **Keep business logic in `Fullobby.Core`** (no XAML dependencies) so it
  stays unit-testable; the `Fullobby.App` layer owns UI and lifecycle.
- **Match the surrounding style** — naming, comment density, and idioms. The
  codebase favours small, well-documented, testable units.
- **Add or update tests** for behaviour changes. Security-critical logic
  (validation, parsing, crypto, updater) must be covered by `Core.Tests`.
- **Don't introduce legacy config migration** — app identifiers are fresh
  throughout (see [CLAUDE.md](CLAUDE.md)).

## Submitting a pull request

1. Fork the repo and create a feature branch off `main`.
2. Make your changes with clear, focused commits.
3. Ensure the build is clean and tests pass:
   ```powershell
   dotnet build src/Fullobby.sln -c Release
   dotnet test src/Fullobby.Core.Tests
   ```
   A clean build produces **0 warnings** — please keep it that way.
4. Open a PR describing **what** changed and **why**. Link the issue it
   resolves and note any user-facing or security-relevant impact.

## Licensing

This project is licensed under the [GNU AGPL-3.0](LICENSE). By contributing, you
agree that your contributions are licensed under the same terms.

## Questions

Open a [GitHub issue](https://github.com/Cat-Tree-Gaming-L-L-C/fullobby-windows/issues)
or ask in the Comp HLL community Discord.
