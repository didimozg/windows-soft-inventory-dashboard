$ErrorActionPreference = 'Stop'

.\src\Install-Server.ps1 `
    -ListenPrefix 'http://+:8080/' `
    -DataPath 'C:\ProgramData\WindowsLicenseInventory\server-data' `
    -OpenFirewall
