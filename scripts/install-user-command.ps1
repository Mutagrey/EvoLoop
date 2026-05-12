$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$runScript = Join-Path $repoRoot 'scripts\run-agent.cmd'
$cliScript = Join-Path $repoRoot 'scripts\run-agent-cli.cmd'
if (-not (Test-Path $runScript)) {
    throw "run-agent.cmd not found: $runScript"
}
if (-not (Test-Path $cliScript)) {
    throw "run-agent-cli.cmd not found: $cliScript"
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
$runScriptLiteral = $runScript.Replace("'", "''")
$cliScriptLiteral = $cliScript.Replace("'", "''")
$snippet = @"
$markerStart
function agent {
    param([Parameter(ValueFromRemainingArguments = `$true)][string[]]`$RemainingArgs)
    & '$runScriptLiteral' --workspace (Get-Location).Path @RemainingArgs
}
function agent-cli {
    param([Parameter(ValueFromRemainingArguments = `$true)][string[]]`$RemainingArgs)
    & '$cliScriptLiteral' --workspace (Get-Location).Path @RemainingArgs
}
$markerEnd
"@

$pattern = '(?s)\r?\n?' + [regex]::Escape($markerStart) + '.*?' + [regex]::Escape($markerEnd)
if ($existing -match [regex]::Escape($markerStart)) {
    $updated = [regex]::Replace($existing, $pattern, "`r`n$snippet", 1)
} elseif ([string]::IsNullOrWhiteSpace($existing)) {
    $updated = "$snippet`r`n"
} else {
    $updated = "$existing`r`n$snippet`r`n"
}

if ($updated -ne $existing) {
    $backupPath = "$profilePath.evoloop.bak.$(Get-Date -Format 'yyyyMMddHHmmss')"
    Copy-Item -Path $profilePath -Destination $backupPath -Force
    Set-Content -Path $profilePath -Value $updated -NoNewline
    Write-Host "Updated PowerShell profile: $profilePath"
} else {
    Write-Host 'PowerShell profile already up to date.'
}

Write-Host 'Open a new terminal, or run:'
Write-Host "  . $profilePath"
