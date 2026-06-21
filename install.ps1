<#
.SYNOPSIS
    Publishes CloudflareDdns and installs it as a Windows Service (LocalSystem).

.DESCRIPTION
    Run from an ELEVATED PowerShell prompt (Run as Administrator).
    Publishes a self-contained-free framework-dependent build to $InstallDir,
    registers the Windows Service, and starts it.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -InstallDir 'C:\Services\CloudflareDdns'
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Program Files\CloudflareDdns',
    [string]$ServiceName = 'CloudflareDdns',
    [string]$DisplayName = 'Cloudflare Dynamic DNS',
    [string]$Description = 'Keeps Cloudflare A records (matching IIS / configured hostnames) pointed at this machine''s public IP.'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Require elevation.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw "This script must be run from an elevated (Administrator) PowerShell prompt."
}

Write-Host "Publishing CloudflareDdns..." -ForegroundColor Cyan
dotnet publish (Join-Path $scriptDir 'CloudflareDdns.csproj') `
    -c Release -r win-x64 --self-contained false `
    -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exePath = Join-Path $InstallDir 'CloudflareDdns.exe'
if (-not (Test-Path $exePath)) { throw "Expected exe not found at $exePath" }

# Register the Event Log source up front (needs admin; the service runs as LocalSystem,
# which can write but not always create the source).
if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    Write-Host "Creating Event Log source '$ServiceName'..." -ForegroundColor Cyan
    New-EventLog -LogName 'Application' -Source $ServiceName
}

# Ensure the data/log directory exists and is writable by LocalSystem (it is by default).
$dataDir = 'C:\ProgramData\CloudflareDdns'
New-Item -ItemType Directory -Force -Path $dataDir, (Join-Path $dataDir 'logs') | Out-Null

# (Re)create the service.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service exists; stopping and reconfiguring..." -ForegroundColor Yellow
    if ($existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force }
    sc.exe config $ServiceName binPath= "`"$exePath`"" start= auto | Out-Null
} else {
    Write-Host "Creating service '$ServiceName'..." -ForegroundColor Cyan
    New-Service -Name $ServiceName -BinaryPathName "`"$exePath`"" `
        -DisplayName $DisplayName -Description $Description -StartupType Automatic | Out-Null
}

# Auto-restart on failure: wait 60s, restart up to 3 times, reset count daily.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service $ServiceName

Write-Host ""
Write-Host "Installed and started '$ServiceName'." -ForegroundColor Green
Write-Host "  Binaries : $InstallDir"
Write-Host "  Config   : $(Join-Path $InstallDir 'appsettings.json')"
Write-Host "  Logs     : $dataDir\logs\ddns-*.log"
Write-Host ""
Write-Host "Edit appsettings.json to set your Cloudflare API token, then restart:" -ForegroundColor Yellow
Write-Host "  Restart-Service $ServiceName"
