<#
.SYNOPSIS
    Stops and removes the CloudflareDdns Windows Service. Run elevated.
.PARAMETER RemoveFiles
    Also delete the installed binaries and the C:\ProgramData\CloudflareDdns data/log folder.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Program Files\CloudflareDdns',
    [string]$ServiceName = 'CloudflareDdns',
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw "This script must be run from an elevated (Administrator) PowerShell prompt."
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..." -ForegroundColor Cyan
        Stop-Service $ServiceName -Force
    }
    Write-Host "Removing service $ServiceName..." -ForegroundColor Cyan
    sc.exe delete $ServiceName | Out-Null
} else {
    Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Yellow
}

if ([System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    Write-Host "Removing Event Log source..." -ForegroundColor Cyan
    Remove-EventLog -Source $ServiceName
}

if ($RemoveFiles) {
    if (Test-Path $InstallDir) {
        Write-Host "Deleting $InstallDir..." -ForegroundColor Cyan
        Remove-Item -Recurse -Force $InstallDir
    }
    $dataDir = 'C:\ProgramData\CloudflareDdns'
    if (Test-Path $dataDir) {
        Write-Host "Deleting $dataDir..." -ForegroundColor Cyan
        Remove-Item -Recurse -Force $dataDir
    }
}

Write-Host "Done." -ForegroundColor Green
