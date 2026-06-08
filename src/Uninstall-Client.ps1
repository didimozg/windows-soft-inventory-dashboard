#requires -Version 2.0

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$InstallPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if (-not $InstallPath) {
    $InstallPath = Join-Path -Path $env:ProgramData -ChildPath 'WindowsLicenseInventory'
}

$serviceName = 'WindowsLicenseInventory'
& sc.exe query $serviceName | Out-Null
if ($LASTEXITCODE -eq 0 -and $PSCmdlet.ShouldProcess($serviceName, 'Stop and delete service')) {
    & sc.exe stop $serviceName | Out-Null
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

if ((Test-Path -LiteralPath $InstallPath) -and $PSCmdlet.ShouldProcess($InstallPath, 'Remove client files')) {
    Remove-Item -LiteralPath $InstallPath -Recurse -Force
}

Write-Host "Client removed."
