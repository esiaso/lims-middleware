# Backup and restore runbook

Targets from the design (section 10.4): RPO 15 minutes, RTO 4 hours, records kept 7 years.

## Two layers

| Layer | What | Recovers from | Script |
|---|---|---|---|
| Physical | Base backup weekly plus continuous WAL archiving | Disk loss, accidental change (point in time) | PostgreSQL config below |
| Logical | `pg_dump -Fc` nightly, AES-256 7z, on-prem plus offsite copy | Server loss, ransomware, long-term retention | `deploy/windows/backup.ps1` |

## WAL archiving (RPO 15 minutes)

In `postgresql.conf` on the database server:

```
wal_level = replica
archive_mode = on
archive_timeout = 300            # force a segment at least every 5 minutes
archive_command = 'copy "%p" "E:\\LimsWal\\%f"'
```

`E:\LimsWal` is a separate volume, copied hourly to the offsite share by Task Scheduler (robocopy /MIR is wrong here because it deletes; use `robocopy /E /XO`). Take a base backup weekly:

```
pg_basebackup -h localhost -U lims_backup -D E:\LimsBase\%DATE% -Ft -z -X stream -P
```

Keep the last 4 base backups and all WAL since the oldest one.

## Nightly logical backup

1. Create the backup account's passphrase file once, as that account:
   `Read-Host -AsSecureString | ConvertFrom-SecureString | Set-Content D:\LimsMiddleware\secrets\backup.pass`
   The file is DPAPI-protected to that account on that machine. Store the same passphrase in the sealed envelope in the lab safe, because without it the offsite archives cannot be opened on another machine.
2. Put the `lims_backup` password in `%APPDATA%\postgresql\pgpass.conf` for the backup account.
3. Schedule `backup.ps1` daily at 01:30. Daily archives are kept 35 days; archives taken on the 1st of the month are kept 7 years.
4. Failures write Application event 3002, which the monitoring agent should alert on.

## Monthly restore drill

Run `restore-test.ps1` on the first Monday of each month. It restores the newest archive into a scratch database, checks the dump checksum, runs `Lims.Host.exe verify-audit` against the restored copy (exit 2 means the audit hash chain is broken), prints row counts, and drops the scratch database. The transcript in `D:\LimsMiddleware\restore-drills` is the evidence for the quarterly review.

## Full recovery (server lost)

1. Build Windows Server 2022, install PostgreSQL 17 and the LIMS MSI (service left stopped).
2. Point in time: restore the latest base backup, set `restore_command` and `recovery_target_time`, start PostgreSQL.
   Logical only: `createdb lims` then `pg_restore -d lims --no-owner <dump>` and rerun `deploy/sql/grants.sql`.
3. `Lims.Host.exe verify-audit --connection "<conn>"` must exit 0.
4. Re-protect secrets on the new machine with `Lims.Host.exe protect-secret` (DPAPI blobs do not move between machines).
5. Start the service. The outbox resumes pending deliveries; the HMIS deduplicates by idempotency key, so a delivery already made before the loss is not duplicated.
6. Run the reconciliation screen and resolve anything older than the restore point by checking against the HMIS and the analyzers.
