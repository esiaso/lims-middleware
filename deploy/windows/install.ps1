<#
.SYNOPSIS
  Installs the LIMS middleware as a Windows Service (design 10.3, steps 4-5).
  Interim script until the signed WiX MSI exists. Run as Administrator on Windows Server 2022.
.EXAMPLE
  .\install.ps1 -PublishDir .\publish -DataDir D:\LimsMiddleware
#>
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [string] $InstallDir = "C:\Program Files\LimsMiddleware",
    [string] $DataDir = "D:\LimsMiddleware",
    [string] $ServiceName = "LimsMiddleware"
)
$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $InstallDir, "$DataDir\data", "$DataDir\logs", "$DataDir\audit-export" | Out-Null
Copy-Item -Path (Join-Path $PublishDir "*") -Destination $InstallDir -Recurse -Force

# Virtual service account: NT SERVICE\LimsMiddleware (no domain, so no gMSA; design 9.3)
$account = "NT SERVICE\$ServiceName"
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $ServiceName -BinaryPathName "`"$InstallDir\Lims.Host.exe`"" -DisplayName "LIMS Middleware" `
        -StartupType AutomaticDelayedStart -Description "HMIS to analyzer middleware"
}
sc.exe config $ServiceName obj= $account | Out-Null
# Recovery: restart after 1 min, 1 min, 5 min; reset after 1 day
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

# ACLs: service reads program files, modifies data and logs; Users get nothing on data
icacls $InstallDir /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" "${account}:(OI)(CI)RX" | Out-Null
icacls $DataDir /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" "${account}:(OI)(CI)M" | Out-Null

Write-Host "Installed. Next: configure.ps1 (DB roles, DPAPI secrets, firewall), then start the service and run verify.ps1."
