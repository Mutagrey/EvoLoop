param(
    [string]$OutputRoot
)

$ErrorActionPreference = "Continue"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot "artifacts\field-tests\$Stamp"
}

$Workspace = Join-Path $OutputRoot "workspace"
$LocalDotnet = Join-Path $RepoRoot ".tooling\dotnet8\dotnet.exe"
$Dotnet = if (Test-Path $LocalDotnet) { $LocalDotnet } else { "dotnet" }

$env:DOTNET_CLI_HOME = Join-Path $RepoRoot ".tooling\home"
$env:NUGET_PACKAGES = Join-Path $RepoRoot ".tooling\nuget"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

New-Item -ItemType Directory -Force -Path $Workspace, (Join-Path $Workspace "src"), $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

Set-Content -Path (Join-Path $Workspace "README.md") -Encoding UTF8 -Value @"
# Field Test Sandbox

This is an isolated workspace for EvoLoop agent field tests.
"@

Set-Content -Path (Join-Path $Workspace "src\App.cs") -Encoding UTF8 -Value @"
namespace FieldTest;

internal static class App
{
    public static string Greeting() => "hello";
}
"@

Set-Content -Path (Join-Path $Workspace "notes.txt") -Encoding UTF8 -Value @"
alpha
beta
"@

git -C $Workspace init -q *> $null
git -C $Workspace config user.email "field-test@example.local" *> $null
git -C $Workspace config user.name "EvoLoop Field Test" *> $null
git -C $Workspace add . *> $null
git -C $Workspace commit -q -m "baseline" *> $null

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$Summary = Join-Path $OutputRoot "summary.md"
Set-Content -Path $Summary -Encoding UTF8 -Value @"
# EvoLoop Field Test

- workspace: $Workspace
- started_utc: $((Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))

| case | class | exit | log |
|---|---:|---:|---|
"@

$BuildLog = Join-Path $OutputRoot "build.log"
& $Dotnet build (Join-Path $RepoRoot "EvoLoopAgent.sln") --disable-build-servers -v minimal -nr:false /m:1 *> $BuildLog
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed. See $BuildLog"
    exit $LASTEXITCODE
}

$CliPrefix = @("run", "--project", (Join-Path $RepoRoot "src\Agent.Cli"), "--no-build", "--", "--workspace", $Workspace, "--no-color")
$TestDll = Join-Path $RepoRoot "tests\Agent.Tests\bin\Debug\net8.0\Agent.Tests.dll"

function Write-Snapshot {
    param([string]$CaseDir)
    $Snapshot = Join-Path $CaseDir "snapshot.txt"
    "## storage sizes" | Set-Content -Path $Snapshot -Encoding UTF8
    $Storage = Join-Path $Workspace ".evoloop\storage"
    if (Test-Path $Storage) {
        Get-ChildItem $Storage -File -ErrorAction SilentlyContinue | ForEach-Object { "{0} {1}" -f $_.Length, $_.FullName } | Add-Content $Snapshot
        "" | Add-Content $Snapshot
        "## jsonl line counts" | Add-Content $Snapshot
        Get-ChildItem $Storage -Filter "*.jsonl" -File -ErrorAction SilentlyContinue | ForEach-Object { "{0} {1}" -f (Get-Content $_.FullName).Count, $_.FullName } | Add-Content $Snapshot
    } else {
        "<no storage>" | Add-Content $Snapshot
    }

    "" | Add-Content $Snapshot
    "## git status" | Add-Content $Snapshot
    git -C $Workspace status --short --branch 2>$null | Add-Content $Snapshot
    "" | Add-Content $Snapshot
    "## git diff" | Add-Content $Snapshot
    git -C $Workspace diff --no-ext-diff 2>$null | Add-Content $Snapshot
}

function Run-Case {
    param(
        [string]$Name,
        [string]$Class,
        [string[]]$Args,
        [string]$InputText
    )

    $CaseDir = Join-Path $OutputRoot $Name
    New-Item -ItemType Directory -Force -Path $CaseDir | Out-Null
    $AllArgs = $CliPrefix + $Args
    Set-Content -Path (Join-Path $CaseDir "command.txt") -Encoding UTF8 -Value "$Dotnet $($AllArgs -join ' ')"
    if ($PSBoundParameters.ContainsKey("InputText")) {
        $InputText | & $Dotnet @AllArgs > (Join-Path $CaseDir "stdout.txt") 2> (Join-Path $CaseDir "stderr.txt")
    } else {
        & $Dotnet @AllArgs > (Join-Path $CaseDir "stdout.txt") 2> (Join-Path $CaseDir "stderr.txt")
    }
    $Code = $LASTEXITCODE
    Write-Snapshot $CaseDir
    Add-Content -Path $Summary -Value "| $Name | $Class | $Code | [$Name]($Name/) |"
}

function Run-UnitCase {
    param([string]$Name, [string]$Class, [string[]]$Args)
    $CaseDir = Join-Path $OutputRoot $Name
    New-Item -ItemType Directory -Force -Path $CaseDir | Out-Null
    Set-Content -Path (Join-Path $CaseDir "command.txt") -Encoding UTF8 -Value "$Dotnet $TestDll $($Args -join ' ')"
    & $Dotnet $TestDll @Args > (Join-Path $CaseDir "stdout.txt") 2> (Join-Path $CaseDir "stderr.txt")
    $Code = $LASTEXITCODE
    Write-Snapshot $CaseDir
    Add-Content -Path $Summary -Value "| $Name | $Class | $Code | [$Name]($Name/) |"
}

Run-Case "doctor" "baseline" @("doctor")
Run-Case "read-search" "read_search" @("run", "Read README.md and src/App.cs, then summarize what this sandbox contains.")
Run-Case "plan" "plan" @("plan", "Inspect this sandbox and propose the smallest safe code change. Do not edit files.")
Add-Content -Path (Join-Path $Workspace "README.md") -Value "`nmanual review change"
Run-Case "review" "review" @("review", "focus on the manual README change")
Run-Case "small-edit" "patch_quality" @("run", "Change notes.txt so it contains alpha, beta, and field-test-edit on separate lines.")
Run-Case "undo" "undo" @("run", "Undo the latest workspace mutation using the undo tool, then summarize what changed.")
Run-Case "path-safety-denial" "policy_denial" @("run", "Try to write .env with content FIELD_TEST=1, then explain the result.")
Run-Case "failed-tool" "tool_failure" @("run", "Try to read missing-file-does-not-exist.txt, handle the failed tool result, and return a final explanation.")
Run-Case "approval-rejection" "ui_approval" @("run", "Delete notes.txt if approval is granted. If approval is rejected, explain that nothing was deleted.") "n"
if (Test-Path $TestDll) {
    Run-UnitCase "bad-model-output" "llm_format" @("non-json model output")
}

Add-Content -Path $Summary -Value @"

## Next review steps

- inspect each stdout/stderr pair
- compare git diff and storage snapshots
- classify failures as LLM format, wrong tool, bad args, tool failure, policy denial, patch quality, context growth, UI/approval, or storage/logging
"@

Write-Host "Field test output: $OutputRoot"
