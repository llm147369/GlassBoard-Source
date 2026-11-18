# GlassBoard startup speed optimizer
# Precompiles GlassBoard.exe (and its bundled DLLs) with NGEN so the next cold
# start skips JIT compilation. Run ONCE as administrator:
#   right-click -> Run with PowerShell  (or)  powershell -File ngenspeed.ps1
# Re-run after replacing GlassBoard.exe with a new build.
$ErrorActionPreference = 'Continue'

$exe = Join-Path $PSScriptRoot 'GlassBoard.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host '[ERROR] GlassBoard.exe not found next to this script.' -ForegroundColor Red
    Read-Host 'Press Enter to exit'
    exit 1
}

$me = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($me)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Administrator rights required for NGEN. Requesting elevation...'
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$ngen = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'ngen.exe'
Write-Host "NGEN: $ngen"

Write-Host 'Precompiling GlassBoard.exe ...'
& $ngen install $exe

Write-Host 'Precompiling bundled DLLs ...'
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.dll' | ForEach-Object {
    Write-Host ("  " + $_.Name)
    & $ngen install $_.FullName | Out-Null
}

Write-Host 'Flushing queued items ...'
& $ngen executeQueuedItems | Out-Null

Write-Host ''
Write-Host 'Done. Next cold start (reboot) should be noticeably faster.' -ForegroundColor Green
Read-Host 'Press Enter to exit'
