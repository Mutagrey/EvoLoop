# TUI Dependency Audit

## Findings

1. `Agent.Tui` now references `Terminal.Gui` directly.
2. `Terminal.Gui` was not present before the audit and is now vendored plus referenced.
3. Local TUI `.nupkg` packages are present under `vendor/nuget`.
4. Repository-level `NuGet.Config` now points normal restore at `vendor/nuget` only; optional online development restore uses `NuGet.online.config`.
5. Package restore was previously online-default because no repo `NuGet.Config` existed.
6. `Directory.Packages.props` centralizes TUI package versions and enables lock files; `Agent.Tui` and tests have updated `packages.lock.json` files.
7. The first TUI implementation uses `Terminal.Gui` through an explicit `PackageReference`.
8. Vendored packages for `Terminal.Gui 1.19.0`: `NStack.Core 1.1.1`, `System.Management 9.0.4`, `System.CodeDom 9.0.4`, `System.ValueTuple 4.5.0`.
9. `Agent.Tui` restores successfully from the local feed using the repository `NuGet.Config`.
10. Nothing is missing for the minimal TUI shell restore.

## Version Decision

Use `Terminal.Gui 1.19.0` for the first implementation. It supports `net8.0` and has a smaller dependency graph than `Terminal.Gui 2.0.0`.

Do not use `Terminal.Gui 2.1.0` while the project targets `net8.0`; it is `net10.0`-only.

## Verification Notes

- `dotnet restore src/Agent.Tui/Agent.Tui.csproj --configfile NuGet.Config --no-cache` succeeded.
- `dotnet restore tests/Agent.Tests/Agent.Tests.csproj --configfile NuGet.Config --no-cache` succeeded.
- `dotnet nuget verify --all` reports expired/distrusted signature metadata for legacy `System.ValueTuple 4.5.0`; restore still succeeds from the local feed.
