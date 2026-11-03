<#
.SYNOPSIS
  Installs the LIMS middleware from the offline bundle on a server with no internet access (design 10.3).
  Order: check every file against SHA256SUMS.txt, install PostgreSQL (unless one is already there or -PgHost
  points elsewhere), install the MSI, run configure.ps1, start the service and run verify.ps1.
  Run as Administrator from the unpacked bundle folder. Read README-OFFLINE.md first.
.EXAMPLE
  .\Install-Offline.ps1 -CertThumbprint 0123... -HmisBaseUrl https://hmis.example/api/lab/v1/ `
      -HmisTokenUrl https://hmis.example/oauth/token -HttpsAllowFrom 10.20.0.0/24 -AnalyzerPorts 5100 -AnalyzerAllowFrom 10.30.0.0/24
#>
param(
    [string] $DataDir = "D:\LimsMiddleware",
    [string] $PgDataDir = "D:\PostgreSQL\17\data",
    [string] $PgHost = "localhost",
    [int] $PgPort = 5432,
    [securestring] $PgSuperPassword,
    [string] $CertThumbprint,
    [int] $HttpsPort = 443,
    [string] $AdminUsername = "admin",
    [securestring] $AdminPassword,
    [string] $HmisBaseUrl,
    [string] $HmisTokenUrl,
    [securestring] $HmisClientSecret,
    [securestring] $HmisWebhookSecret,
    [string] $BackupDir,
    [string[]] $HttpsAllowFrom,
    [int[]] $AnalyzerPorts,
    [string[]] $AnalyzerAllowFrom,
    # Base URL for verify.ps1; with a hospital certificate use the name on the certificate.
    [string] $VerifyUrl,
    [switch] $NoStart
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3
$root = $PSScriptRoot
$log = Join-Path $root ("install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
Start-Transcript -Path $log | Out-Null
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run as Administrator."
    }
    $bundle = Get-Content -Raw (Join-Path $root "bundle.json") | ConvertFrom-Json
    Write-Host "LIMS middleware $($bundle.version) offline bundle (commit $($bundle.commit), built $($bundle.builtAt))"

    # 1. Integrity: every file listed must match, and nothing may be missing.
    Write-Host "Checking file hashes"
    $bad = @()
    foreach ($line in Get-Content (Join-Path $root "SHA256SUMS.txt")) {
        if (-not $line.Trim()) { continue }
        $hash, $rel = $line -split "  ", 2
        $file = Join-Path $root $rel
        if (-not (Test-Path -LiteralPath $file)) { $bad += "missing: $rel"; continue }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash -ne $hash.ToUpperInvariant()) { $bad += "changed: $rel" }
    }
    if ($bad) { throw "The bundle is damaged or was modified; do not install it.`n$($bad -join "`n")" }

    # Signatures. A server without internet access may not be able to build or check the certificate chain, so only a
    # broken or missing signature, or one from another certificate, stops the install; other statuses are warnings.
    # The hashes above already prove the files are the ones that were built.
    function Test-Signature([string] $file, [string] $expectedThumbprint) {
        $sig = Get-AuthenticodeSignature $file
        if ($sig.Status -in "HashMismatch", "NotSigned" -or ($expectedThumbprint -and $sig.SignerCertificate.Thumbprint -ne $expectedThumbprint)) {
            throw "The signature of $(Split-Path $file -Leaf) is $($sig.Status) or from another certificate; do not install it."
        }
        if ($sig.Status -ne "Valid") { Write-Warning "$(Split-Path $file -Leaf): signature status $($sig.Status) on this server ($($sig.StatusMessage))" }
        else { Write-Host "$(Split-Path $file -Leaf) signed by $($sig.SignerCertificate.Subject)" }
    }
    if ($bundle.signed.msi) { Test-Signature (Join-Path $root $bundle.msi) $bundle.signed.signerThumbprint }
    else { Write-Warning "This bundle's MSI is not code-signed." }
    Test-Signature (Join-Path $root $bundle.postgres.file) $null

    # 2. PostgreSQL
    $pg = $bundle.postgres
    $pgService = "postgresql-x64-$($pg.major)"
    $pgPrefix = "C:\Program Files\PostgreSQL\$($pg.major)"
    $pgBin = Join-Path $pgPrefix "bin"
    if (-not $PgSuperPassword) { $PgSuperPassword = Read-Host "PostgreSQL superuser password (new install: choose one and give it to hospital IT; it is not stored)" -AsSecureString }
    if ($PgHost -ne "localhost") {
        Write-Host "Using the PostgreSQL server at ${PgHost}:$PgPort; the local client tools are still installed."
    }
    if (Get-Service $pgService -ErrorAction SilentlyContinue) {
        Write-Host "PostgreSQL service $pgService already installed; skipping."
    } else {
        Write-Host "Installing PostgreSQL $($pg.version)"
        $opt = Join-Path $env:TEMP ("pg-options-{0}.txt" -f [guid]::NewGuid())
        New-Item -ItemType File -Path $opt | Out-Null
        icacls $opt /inheritance:r /grant:r "Administrators:F" "SYSTEM:F" | Out-Null
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PgSuperPassword)
        try {
            @(
                "mode=unattended", "unattendedmodeui=none", "prefix=$pgPrefix", "datadir=$PgDataDir",
                "serverport=$PgPort", "servicename=$pgService", "install_runtimes=1",
                "enable-components=server,commandlinetools", "disable-components=pgAdmin,stackbuilder",
                "superpassword=$([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr))"
            ) | Set-Content -Encoding ASCII $opt
        } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        try {
            $p = Start-Process (Join-Path $root $pg.file) -ArgumentList "--optionfile `"$opt`"" -Wait -PassThru
            if ($p.ExitCode -ne 0) { throw "PostgreSQL installer failed: exit $($p.ExitCode). See %TEMP%\install-postgresql.log" }
        } finally { Remove-Item $opt -Force -ErrorAction SilentlyContinue }
        # Local database only: listen on loopback (design 9.2), then restart.
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PgSuperPassword)
        try { $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        try {
            & (Join-Path $pgBin "psql.exe") -h localhost -p $PgPort -U postgres -d postgres -v ON_ERROR_STOP=1 -q -c "ALTER SYSTEM SET listen_addresses = 'localhost'"
            if ($LASTEXITCODE -ne 0) { throw "could not set listen_addresses" }
        } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
        Restart-Service $pgService
    }
    if (-not (Test-Path (Join-Path $pgBin "psql.exe"))) { throw "psql.exe not found in $pgBin" }

    # 3. MSI (service registered, left stopped)
    $msi = Join-Path $root $bundle.msi
    Write-Host "Installing $($bundle.msi)"
    $msiArgs = "/i `"$msi`" /qn /l*v `"$(Join-Path $root 'msi-install.log')`" DATADIR=`"$DataDir`" HTTPSPORT=$HttpsPort"
    if ($CertThumbprint) { $msiArgs += " CERTTHUMBPRINT=$($CertThumbprint -replace '\s','')" }
    $p = Start-Process msiexec -ArgumentList $msiArgs -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "MSI install failed: exit $($p.ExitCode). See msi-install.log" }
    if ($p.ExitCode -eq 3010) { Write-Warning "Windows asks for a restart; finish this script first." }

    # 4. Configure (roles, migrations, grants, secrets, certificate key, firewall)
    $cfg = @{
        PgBin = $pgBin; PgHost = $PgHost; PgPort = $PgPort; PgSuperPassword = $PgSuperPassword; DataDir = $DataDir
        SqlDir = (Join-Path $root "scripts\sql"); HttpsPort = $HttpsPort; AdminUsername = $AdminUsername
    }
    foreach ($k in "AdminPassword", "CertThumbprint", "HmisBaseUrl", "HmisTokenUrl", "HmisClientSecret", "HmisWebhookSecret",
                   "BackupDir", "HttpsAllowFrom", "AnalyzerPorts", "AnalyzerAllowFrom") {
        $v = Get-Variable $k -ValueOnly
        if ($v) { $cfg[$k] = $v }
    }
    & (Join-Path $root "scripts\windows\configure.ps1") @cfg

    # 5. Start and verify
    if ($NoStart) { Write-Host "Not started (-NoStart). Start-Service LimsMiddleware, then scripts\windows\verify.ps1."; return }
    Start-Service LimsMiddleware
    if (-not $VerifyUrl) { $VerifyUrl = if ($CertThumbprint) { "https://localhost:$HttpsPort" } else { "http://localhost:5000" } }
    $deadline = (Get-Date).AddMinutes(2)
    while ($true) {
        try { Invoke-RestMethod "$VerifyUrl/health/live" -TimeoutSec 5 | Out-Null; break }
        catch { if ((Get-Date) -gt $deadline) { throw "Service did not answer at $VerifyUrl within 2 minutes; see the Application event log (source LimsMiddleware)." }; Start-Sleep 3 }
    }
    & (Join-Path $root "scripts\windows\verify.ps1") -BaseUrl $VerifyUrl
    Write-Host ""
    Write-Host "Installed. Next: sign in as '$AdminUsername', create named accounts and deactivate the bootstrap administrator;"
    Write-Host "schedule scripts\windows\backup.ps1 (docs\BACKUP-RUNBOOK.md). Log: $log"
} finally {
    Stop-Transcript | Out-Null
}
