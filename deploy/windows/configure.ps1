<#
.SYNOPSIS
  Configures an installed LIMS middleware (design 10.3 steps 6-8): database roles, migrations, grants,
  DPAPI-protected secrets in appsettings.Local.json, certificate key access and firewall rules.
  Run as Administrator after the MSI, before starting the service. Safe to run again: role passwords are
  regenerated and the secrets file is rewritten.
.EXAMPLE
  .\configure.ps1 -PgBin "C:\Program Files\PostgreSQL\17\bin" -CertThumbprint 0123... `
      -HmisBaseUrl https://hmis.example/api/lab/v1/ -HmisTokenUrl https://hmis.example/oauth/token `
      -HttpsAllowFrom 10.20.0.0/24 -AnalyzerPorts 5100 -AnalyzerAllowFrom 10.30.0.0/24
#>
param(
    [Parameter(Mandatory)] [string] $PgBin,
    [string] $PgHost = "localhost",
    [int] $PgPort = 5432,
    [string] $PgSuperUser = "postgres",
    [securestring] $PgSuperPassword,
    [string] $InstallDir = "C:\Program Files\LimsMiddleware",
    [string] $DataDir = "D:\LimsMiddleware",
    [string] $SqlDir = (Join-Path $PSScriptRoot "..\sql"),
    [string] $ServiceName = "LimsMiddleware",
    [string] $CertThumbprint,
    [int] $HttpsPort = 443,
    [string] $AdminUsername = "admin",
    [securestring] $AdminPassword,
    [string] $HmisBaseUrl,
    [string] $HmisTokenUrl,
    [securestring] $HmisClientSecret,
    [securestring] $HmisWebhookSecret,
    [string] $BackupDir,
    # Firewall (design 9.2): staff VLAN for HTTPS, analyzer VLAN for listening instrument ports. Rules are only
    # created when the remote addresses are given; nothing is opened to "Any".
    [string[]] $HttpsAllowFrom,
    [int[]] $AnalyzerPorts,
    [string[]] $AnalyzerAllowFrom
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3
# Text piped to Lims.Host.exe: UTF-8 without a byte order mark.
$OutputEncoding = New-Object Text.UTF8Encoding $false

function Plain([securestring] $s) {
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}
function New-Password {
    # Letters and digits only, so the value never needs quoting on a command line or in a connection string.
    $chars = [char[]]"ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}
function Protect([string] $value) {
    $out = $value | & (Join-Path $InstallDir "Lims.Host.exe") protect-secret
    if ($LASTEXITCODE -ne 0 -or -not ($out -like "dpapi-machine:*")) { throw "protect-secret failed" }
    "$out".Trim()
}
function Invoke-Psql([string] $user, [string] $password, [string] $database, [string[]] $psqlArgs) {
    $env:PGPASSWORD = $password
    try {
        & (Join-Path $PgBin "psql.exe") -h $PgHost -p $PgPort -U $user -d $database -v ON_ERROR_STOP=1 -q @psqlArgs
        if ($LASTEXITCODE -ne 0) { throw "psql failed ($user on $database): exit $LASTEXITCODE" }
    } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}
function Set-Json($obj, [string] $path, $value) {
    $parts = $path.Split(":")
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        if (-not ($obj.PSObject.Properties.Name -contains $parts[$i])) { $obj | Add-Member -NotePropertyName $parts[$i] -NotePropertyValue ([pscustomobject]@{}) }
        $obj = $obj.($parts[$i])
    }
    $leaf = $parts[-1]
    if ($obj.PSObject.Properties.Name -contains $leaf) { $obj.$leaf = $value } else { $obj | Add-Member -NotePropertyName $leaf -NotePropertyValue $value }
}
function Grant-CertificateKey([string] $thumbprint, [string] $account) {
    $cert = Get-Item "Cert:\LocalMachine\My\$($thumbprint -replace '\s','')" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "Certificate $thumbprint not found in LocalMachine\My" }
    if (-not $cert.HasPrivateKey) { throw "Certificate $thumbprint has no private key" }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    $name = $null
    if ($rsa -is [Security.Cryptography.RSACng]) { $name = $rsa.Key.UniqueName; $dirs = "$env:ProgramData\Microsoft\Crypto\Keys" }
    elseif ($rsa.PSObject.Properties.Name -contains "CspKeyContainerInfo") { $name = $rsa.CspKeyContainerInfo.UniqueKeyContainerName; $dirs = "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" }
    if (-not $name) { throw "Cannot locate the private key of $thumbprint (only RSA keys are handled)" }
    $file = Join-Path $dirs $name
    if (-not (Test-Path $file)) { $file = Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $name }
    icacls $file /grant "${account}:R" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls on the certificate key failed" }
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run as Administrator."
}
if (-not (Test-Path (Join-Path $InstallDir "Lims.Host.exe"))) { throw "Lims.Host.exe not found in $InstallDir; install the MSI first." }
if (-not $PgSuperPassword) { $PgSuperPassword = Read-Host "Password for PostgreSQL superuser '$PgSuperUser' (not stored)" -AsSecureString }
if (-not $AdminPassword) { $AdminPassword = Read-Host "Password for the bootstrap web administrator '$AdminUsername' (12 characters or more)" -AsSecureString }
if ((Plain $AdminPassword).Length -lt 12) { throw "The administrator password must be at least 12 characters." }
$account = "NT SERVICE\$ServiceName"

# 1. Roles and database (roles.sql is idempotent: existing roles get new passwords).
Write-Host "Creating database roles and the lims database"
$ownerPw = New-Password; $appPw = New-Password; $roPw = New-Password
Invoke-Psql $PgSuperUser (Plain $PgSuperPassword) "postgres" @("-v", "owner_pw=$ownerPw", "-v", "app_pw=$appPw", "-v", "ro_pw=$roPw", "-f", (Join-Path $SqlDir "roles.sql"))

# 2. Migrations as lims_owner, then grants. The owner connection is passed as a DPAPI reference, never in plain text.
$base = "Host=$PgHost;Port=$PgPort;Database=lims"
$ownerRef = Protect "$base;Username=lims_owner;Password=$ownerPw"
Write-Host "Applying migrations"
& (Join-Path $InstallDir "Lims.Host.exe") migrate --connection $ownerRef
if ($LASTEXITCODE -ne 0) { throw "migrate failed: exit $LASTEXITCODE" }
Invoke-Psql "lims_owner" $ownerPw "lims" @("-f", (Join-Path $SqlDir "grants.sql"))

# 3. Admin-only copies of the owner and read-only connections, for upgrades (migrate) and reporting.
$adminDir = Join-Path $DataDir "admin"
New-Item -ItemType Directory -Force -Path $adminDir | Out-Null
icacls $adminDir /inheritance:r /grant:r "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" | Out-Null
[ordered]@{
    owner    = $ownerRef
    readonly = (Protect "$base;Username=lims_readonly;Password=$roPw")
    note     = "dpapi-machine references; use with: Lims.Host.exe migrate --connection <owner>"
} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $adminDir "db-connections.json")

# 4. Service settings with protected secrets.
$settingsPath = Join-Path $InstallDir "appsettings.Local.json"
$settings = if (Test-Path $settingsPath) { Get-Content -Raw $settingsPath | ConvertFrom-Json } else { [pscustomobject]@{} }
Set-Json $settings "ConnectionStrings:Lims" (Protect "$base;Username=lims_app;Password=$appPw")
Set-Json $settings "Bootstrap:AdminUsername" $AdminUsername
Set-Json $settings "Bootstrap:AdminPassword" (Protect (Plain $AdminPassword))
if ($HmisBaseUrl) { Set-Json $settings "Hmis:BaseUrl" $HmisBaseUrl }
if ($HmisTokenUrl) { Set-Json $settings "Hmis:TokenUrl" $HmisTokenUrl }
if ($HmisClientSecret) { Set-Json $settings "Hmis:ClientSecret" (Protect (Plain $HmisClientSecret)) }
if ($HmisWebhookSecret) { Set-Json $settings "Hmis:WebhookSecret" (Protect (Plain $HmisWebhookSecret)) }
if ($BackupDir) { Set-Json $settings "Backup:Directory" $BackupDir }
$settings | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 $settingsPath
Write-Host "Wrote $settingsPath"
if (-not $HmisClientSecret -or -not $HmisWebhookSecret) {
    Write-Warning "HMIS secrets not given: the service starts, but HMIS calls fail until they are set (run configure.ps1 again with -HmisClientSecret/-HmisWebhookSecret)."
}

# 5. HTTPS certificate: the virtual service account needs read access to the private key.
if ($CertThumbprint) { Grant-CertificateKey $CertThumbprint $account; Write-Host "Granted $account read access to the certificate key" }
else { Write-Warning "No -CertThumbprint: the service will serve plain HTTP on localhost only." }

# 6. Firewall rules, only for the addresses given.
if ($HttpsAllowFrom) {
    Get-NetFirewallRule -DisplayName "LIMS Middleware HTTPS" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName "LIMS Middleware HTTPS" -Direction Inbound -Protocol TCP -LocalPort $HttpsPort -RemoteAddress $HttpsAllowFrom -Action Allow | Out-Null
} else { Write-Warning "No -HttpsAllowFrom: no inbound HTTPS rule created." }
if ($AnalyzerPorts -and $AnalyzerAllowFrom) {
    Get-NetFirewallRule -DisplayName "LIMS Middleware analyzers" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName "LIMS Middleware analyzers" -Direction Inbound -Protocol TCP -LocalPort $AnalyzerPorts -RemoteAddress $AnalyzerAllowFrom -Action Allow | Out-Null
} elseif ($AnalyzerPorts) { Write-Warning "-AnalyzerPorts given without -AnalyzerAllowFrom: no analyzer rule created." }

Write-Host "Configured. Start the service (Start-Service $ServiceName), then run verify.ps1."
