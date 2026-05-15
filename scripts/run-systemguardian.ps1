$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$appExe = Join-Path $repoRoot "src\SystemGuardian.App\bin\Debug\net8.0\SystemGuardian.App.exe"
$logDir = Join-Path $repoRoot "logs"
$logFile = Join-Path $logDir "systemguardian-runtime.log"

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$running = Get-CimInstance Win32_Process -Filter "Name = 'SystemGuardian.App.exe'" |
    Where-Object { $_.ExecutablePath -eq $appExe }

if ($running) {
    $timestamp = Get-Date -Format "o"
    Add-Content -Path $logFile -Value "$timestamp [STARTUP] Existing SystemGuardian instance detected; runner exiting."
    exit 0
}

$env:DOTNET_ROLL_FORWARD = "Major"
Set-Location -LiteralPath $repoRoot

$timestamp = Get-Date -Format "o"
Add-Content -Path $logFile -Value "$timestamp [STARTUP] Launching SystemGuardian from scheduled runner."

& $appExe *>> $logFile
