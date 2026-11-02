<#
.SYNOPSIS
  Monthly restore drill (design 10.4): restore the newest backup into a scratch database,
  verify the audit hash chain with Lims.Host.exe verify-audit, record row counts, drop the scratch DB.
  Exit code 0 = drill passed. Keep the transcript as evidence for the quarterly review.
#>
param(
    [Parameter(Mandatory)] [string] $BackupDir,
    [Parameter(Mandatory)] [string] $PassphraseFile,
    [string] $PgHost = "localhost",
    [int]    $PgPort = 5432,
    [string] $PgUser = "postgres",
    [string] $PgBin = "C:\Program Files\PostgreSQL\17\bin",
    [string] $SevenZip = "C:\Program Files\7-Zip\7z.exe",
    [string] $LimsExe = "C:\Program Files\LimsMiddleware\Lims.Host.exe",
    [string] $EvidenceDir = "D:\LimsMiddleware\restore-drills"
)
$ErrorActionPreference = "Stop"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
Start-Transcript -Path (Join-Path $EvidenceDir "restore-drill-$stamp.log")
$work = Join-Path $env:TEMP "lims-restore-$stamp"
$scratch = "lims_restore_$($stamp -replace '-', '_')"
$psql = Join-Path $PgBin "psql.exe"
$exit = 1
try {
    $archive = Get-ChildItem $BackupDir -Recurse -Filter "*.7z" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $archive) { throw "No backup archives under $BackupDir" }
    Write-Host "Restoring $($archive.FullName) into $scratch"

    $secure = Get-Content $PassphraseFile | ConvertTo-SecureString
    $pass = [System.Net.NetworkCredential]::new("", $secure).Password
    & $SevenZip x "-p$pass" "-o$work" $archive.FullName | Out-Null
    $pass = $null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip extract failed" }

    $dump = Get-ChildItem $work -Filter "*.dump" | Select-Object -First 1
    $expected = (Get-Content "$($dump.FullName).sha256").Split(" ")[0]
    if ((Get-FileHash -Algorithm SHA256 $dump.FullName).Hash -ne $expected) { throw "Dump checksum mismatch" }

    & $psql -h $PgHost -p $PgPort -U $PgUser -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $scratch"
    & (Join-Path $PgBin "pg_restore.exe") -h $PgHost -p $PgPort -U $PgUser -d $scratch --no-owner --exit-on-error $dump.FullName
    if ($LASTEXITCODE -ne 0) { throw "pg_restore failed" }

    $conn = "Host=$PgHost;Port=$PgPort;Username=$PgUser;Database=$scratch"
    & $LimsExe verify-audit --connection $conn
    if ($LASTEXITCODE -ne 0) { throw "Audit chain verification FAILED on the restored copy (exit $LASTEXITCODE)" }

    & $psql -h $PgHost -p $PgPort -U $PgUser -d $scratch -c @"
SELECT 'lab_order' AS t, count(*) FROM clinical.lab_order UNION ALL
SELECT 'result', count(*) FROM clinical.result UNION ALL
SELECT 'audit_event', count(*) FROM audit.audit_event UNION ALL
SELECT 'outbox_pending', count(*) FROM messaging.outbox_message WHERE status <> 'confirmed';
"@
    Write-Host "RESTORE DRILL PASSED"
    $exit = 0
}
catch { Write-Host "RESTORE DRILL FAILED: $_" }
finally {
    & $psql -h $PgHost -p $PgPort -U $PgUser -d postgres -c "DROP DATABASE IF EXISTS $scratch" | Out-Null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    Stop-Transcript
}
exit $exit
