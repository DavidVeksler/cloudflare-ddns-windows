<#
.SYNOPSIS
    Builds and launches the Cloudflare DDNS control panel (WPF GUI).

.DESCRIPTION
    A convenience wrapper so you don't have to remember the project path. Runs a normal
    (non-elevated) build + launch; the app requests elevation per-action for service control.

.EXAMPLE
    .\run-gui.ps1
    .\run-gui.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $scriptDir 'gui\CloudflareDdns.Gui.csproj'

Write-Host "Building control panel ($Configuration)..." -ForegroundColor Cyan
dotnet build $proj -c $Configuration -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$exe = Join-Path $scriptDir "gui\bin\$Configuration\net8.0-windows\CloudflareDdns.Gui.exe"
if (-not (Test-Path $exe)) { throw "Built exe not found at $exe" }

Write-Host "Launching $exe" -ForegroundColor Green
Start-Process -FilePath $exe
