# Dependencies

Normal repository restore uses the local package feed in `vendor/nuget`.

| Package | Version | Purpose | License | Source |
| --- | --- | --- | --- | --- |
| Terminal.Gui | 1.19.0 | Prepared TUI framework for future fullscreen terminal UI | MIT | nuget.org / github.com/gui-cs/Terminal.Gui |
| NStack.Core | 1.1.1 | Terminal.Gui text/unicode dependency | MIT | nuget.org / github.com/gui-cs/NStack |
| System.Management | 9.0.4 | Terminal.Gui Windows management dependency | MIT | nuget.org / github.com/dotnet/runtime |
| System.CodeDom | 9.0.4 | Transitive dependency of System.Management | MIT | nuget.org / github.com/dotnet/runtime |
| System.ValueTuple | 4.5.0 | Transitive dependency of NStack.Core | Microsoft/.NET license metadata | nuget.org / dotnet |

These packages are vendored for preparation only. The current code does not reference `Terminal.Gui` until the minimal TUI shell is implemented.

Current solution locked restore and a temporary `net8.0` `Terminal.Gui` restore both succeed with the repository `NuGet.Config`. `System.ValueTuple 4.5.0` has old signature metadata that fails strict `dotnet nuget verify --all`, but local restore succeeds.
