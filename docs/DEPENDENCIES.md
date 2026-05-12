# Dependencies

Normal repository restore uses the local package feed in `vendor/nuget`.

| Package | Version | Purpose | License | Source |
| --- | --- | --- | --- | --- |
| Terminal.Gui | 1.19.0 | Fullscreen TUI framework for `Agent.Tui` | MIT | nuget.org / github.com/gui-cs/Terminal.Gui |
| NStack.Core | 1.1.1 | Terminal.Gui text/unicode dependency | MIT | nuget.org / github.com/gui-cs/NStack |
| System.Management | 9.0.4 | Terminal.Gui Windows management dependency | MIT | nuget.org / github.com/dotnet/runtime |
| System.CodeDom | 9.0.4 | Transitive dependency of System.Management | MIT | nuget.org / github.com/dotnet/runtime |
| System.ValueTuple | 4.5.0 | Transitive dependency of NStack.Core | Microsoft/.NET license metadata | nuget.org / dotnet |

`Agent.Tui` references `Terminal.Gui` directly. Restore remains offline-first through the repository `NuGet.Config` and vendored packages.

Current `Agent.Tui` restore succeeds with the repository `NuGet.Config`. `System.ValueTuple 4.5.0` has old signature metadata that fails strict `dotnet nuget verify --all`, but local restore succeeds.
