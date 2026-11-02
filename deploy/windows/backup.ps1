<#
.SYNOPSIS
  Nightly logical backup of the LIMS database (design 10.4). Schedule with Task Scheduler as the backup account.
  pg_dump -Fc -> 7-Zip AES-256 archive -> on-prem target and offsite target -> retention pruning.
  Physical recovery (WAL archiving / PITR) is configured on the PostgreSQL server; see docs/BACKUP-RUNBOOK.md.
.EXAMPLE
  .\backup.ps1 -Database lims -LocalTarget E:\LimsBackup -OffsiteTarget \\backup-nas\lims -PassphraseFile D:\LimsMiddleware\secrets\backup.pass
#>
param(
    [string] $Database = "lims",
    [string] $PgHost = "localhost",
    [int]    $PgPort = 5432,
    [string] $PgUser = "lims_backup",
    [string] $PgBin = "C:\Program Files\PostgreSQL\17\bin",
    [string] $SevenZip = "C:\Program Files\7-Zip\7z.exe",
    [Parameter(Mandatory)] [string] $LocalTarget,
    [string] $OffsiteTarget,
    # File holding a DPAPI-protected passphrase (ConvertFrom-SecureString output). Never a plain-text file.
    [Parameter(Mandatory)] [string] $PassphraseFile,
    [int] $DailyKeep = 35,
    [int] $MonthlyKeepYears = 7
)
$ErrorActionPreference = "Stop"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$work = Join-Path $env:TEMP "lims-backup-$stamp"
New-Item -ItemType Directory -Force -Path $work, $LocalTarget | Out-Null

try {
    $dump = Join-Path $work "$Database-$stamp.dump"
    # Password comes from %APPDATA%\postgresql\pgpass.conf of the backup account (ACL'd to that account only).
    & (Join-Path $PgBin "pg_dump.exe") -h $PgHost -p $PgPort -U $PgUser -Fc -Z 6 -f $dump $Database
    if ($LASTEXITCODE -ne 0) { throw "pg_dump failed with exit code $LASTEXITCODE" }

    $hash = (Get-FileHash -Algorithm SHA256 $dump).Hash
    Set-Content -Path "$dump.sha256" -Value "$hash  $(Split-Path -Leaf $dump)"

    $secure = Get-Content $PassphraseFile | ConvertTo-SecureString
    $pass = [System.Net.NetworkCredential]::new("", $secure).Password
    $archive = Join-Path $work "$Database-$stamp.7z"
    # -mhe=on encrypts file names too; AES-256 is 7z's cipher for the 7z format.
    & $SevenZip a -t7z -mhe=on -mx=1 "-p$pass" $archive $dump "$dump.sha256" | Out-Null
    $pass = $null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed with exit code $LASTEXITCODE" }

    $isMonthly = (Get-Date).Day -eq 1
    $kind = if ($isMonthly) { "monthly" } else { "daily" }
    foreach ($target in @($LocalTarget, $OffsiteTarget) | Where-Object { $_ }) {
        $dest = Join-Path $target $kind
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Copy-Item $archive $dest
        $copied = Join-Path $dest (Split-Path -Leaf $archive)
        if ((Get-FileHash $copied).Hash -ne (Get-FileHash $archive).Hash) { throw "Copy verification failed for $copied" }

        Get-ChildItem (Join-Path $target "daily") -Filter "*.7z" -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$DailyKeep) } | Remove-Item
        Get-ChildItem (Join-Path $target "monthly") -Filter "*.7z" -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -lt (Get-Date).AddYears(-$MonthlyKeepYears) } | Remove-Item
    }
    Write-EventLog -LogName Application -Source "LimsMiddleware" -EventId 3001 -EntryType Information `
        -Message "Backup $kind completed: $(Split-Path -Leaf $archive) sha256(dump)=$hash" -ErrorAction SilentlyContinue
    Write-Host "Backup completed: $archive ($kind)"
}
catch {
    Write-EventLog -LogName Application -Source "LimsMiddleware" -EventId 3002 -EntryType Error `
        -Message "Backup FAILED: $_" -ErrorAction SilentlyContinue
    throw
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
