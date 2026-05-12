# Vendored NuGet Packages

This folder is the repository-local NuGet feed for offline-first restore.

Packages:

| Package | Version | Why | License | Transitive |
| --- | --- | --- | --- | --- |
| Terminal.Gui | 1.19.0 | Fullscreen TUI framework for `Agent.Tui` | MIT | yes |
| NStack.Core | 1.1.1 | Terminal.Gui dependency | MIT | yes |
| System.Management | 9.0.4 | Terminal.Gui dependency | MIT | yes |
| System.CodeDom | 9.0.4 | System.Management dependency | MIT | no |
| System.ValueTuple | 4.5.0 | NStack.Core dependency | Microsoft/.NET package metadata | no |

Do not add online-only package references. Add `.nupkg` files here first, update `Directory.Packages.props`, then restore through the repository `NuGet.Config`.

Note: `System.ValueTuple 4.5.0` has legacy signature metadata that can fail strict package signature verification on modern macOS trust stores. It is retained because it is declared by `NStack.Core`; local offline restore succeeds.
