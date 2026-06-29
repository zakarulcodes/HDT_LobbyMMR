# Builds HDT_LobbyMMR and copies it into the HDT plugins folder.
# Usage:  powershell -File deploy.ps1
$ErrorActionPreference = "Stop"

$proj    = Join-Path $PSScriptRoot "HDT_LobbyMMR.csproj"
$dll     = Join-Path $PSScriptRoot "bin\Release\HDT_LobbyMMR.dll"
$plugins = Join-Path $env:APPDATA "HearthstoneDeckTracker\Plugins\HDT_LobbyMMR"

Write-Host "Building (Release)..." -ForegroundColor Cyan
dotnet build $proj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if (Get-Process -Name "HearthstoneDeckTracker" -ErrorAction SilentlyContinue) {
    Write-Warning "HDT is running. Close it before deploying, then re-run this script."
    Write-Warning "(Windows locks loaded plugin DLLs, so the copy would fail.)"
    return
}

New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Copy-Item $dll -Destination $plugins -Force
Write-Host "Deployed to: $plugins" -ForegroundColor Green
Write-Host "Start HDT, then enable it under Options - Tracker - Plugins (Lobby MMR)." -ForegroundColor Green
