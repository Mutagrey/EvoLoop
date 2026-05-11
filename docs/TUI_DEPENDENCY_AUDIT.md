# TUI Dependency Audit

## Findings

1. No TUI or third-party console library is currently referenced by project files.
2. `Terminal.Gui` was not present before this audit.
3. Local TUI `.nupkg` packages are now prepared under `vendor/nuget`.
4. Repository-level `NuGet.Config` now points normal restore at `vendor/nuget` only; optional online development restore uses `NuGet.online.config`.
5. Package restore was previously online-default because no repo `NuGet.Config` existed.
6. `Directory.Packages.props` now centralizes prepared TUI package versions and enables lock files; current projects have generated `packages.lock.json` files.
7. The first TUI implementation should use `Terminal.Gui` only after adding an explicit `PackageReference`.
8. Vendored packages prepared for `Terminal.Gui 1.19.0`: `NStack.Core 1.1.1`, `System.Management 9.0.4`, `System.CodeDom 9.0.4`, `System.ValueTuple 4.5.0`.
9. The current solution restores in locked mode using only `vendor/nuget`. A temporary `net8.0` project also restored `Terminal.Gui 1.19.0` successfully from the local feed.
10. Missing for full TUI restore: add a `PackageReference Include="Terminal.Gui"` when implementing the minimal TUI shell, then update the relevant `packages.lock.json` from the local feed.

## Version Decision

Use `Terminal.Gui 1.19.0` for the first implementation. It supports `net8.0` and has a smaller dependency graph than `Terminal.Gui 2.0.0`.

Do not use `Terminal.Gui 2.1.0` while the project targets `net8.0`; it is `net10.0`-only.

## Verification Notes

- `dotnet restore EvoLoopAgent.sln --configfile NuGet.Config --no-cache --locked-mode` succeeded.
- `dotnet restore` for a temporary `net8.0` project with `Terminal.Gui 1.19.0` succeeded with `--configfile NuGet.Config` and no online package source.
- `dotnet nuget verify --all` reports expired/distrusted signature metadata for legacy `System.ValueTuple 4.5.0`; restore still succeeds from the local feed.
