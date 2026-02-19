$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$runScript = Join-Path $repoRoot 'scripts\run-agent.cmd'
if (-not (Test-Path $runScript)) {
    throw "run-agent.cmd not found: $runScript"
}

$profilePath = $PROFILE.CurrentUserCurrentHost
$profileDir = Split-Path -Path $profilePath -Parent
if (-not (Test-Path $profileDir)) {
    New-Item -ItemType Directory -Path $profileDir -Force | Out-Null
}
if (-not (Test-Path $profilePath)) {
    New-Item -ItemType File -Path $profilePath -Force | Out-Null
}

$markerStart = '# >>> EvoLoop agent >>>'
$markerEnd = '# <<< EvoLoop agent <<<'
$existing = Get-Content -Path $profilePath -Raw
if ($existing -notmatch [regex]::Escape($markerStart)) {
    $snippet = @"
$markerStart
function agent {
    param([Parameter(ValueFromRemainingArguments = `$true)][string[]]`$Args)
    & '$runScript' --workspace (Get-Location).Path @Args
}
$markerEnd
"@
    Add-Content -Path $profilePath -Value "`r`n$snippet"
    Write-Host "Updated PowerShell profile: $profilePath"
} else {
    Write-Host 'PowerShell profile already configured.'
}

Write-Host 'Open a new terminal, or run:'
Write-Host "  . $profilePath"
