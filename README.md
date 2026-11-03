# LIMS middleware

Middleware between the hospital's in-house HMIS (Laravel/MySQL) and the laboratory analyzers. It registers HMIS
orders, accessions specimens and prints Zebra labels, talks to analyzers through versioned adapters, holds every
result for human approval, and delivers approved results to the HMIS with idempotent, confirmed delivery.

It implements the design document "LIMS Middleware Design: HMIS to Analyzers": Phase 1 (MVP), the go-live items,
the cobas c 111 and Mindray BC-5000 adapters, the remaining screens of section 8, and autoverification (off at install).

> **Compatibility status.** Every analyzer adapter is built from the vendor's manual and is **DOCUMENTED, not
> interface-tested** on an installed analyzer:
>
> | Analyzer | Source document | Protocol |
> | --- | --- | --- |
> | Roche cobas e 411 | cobas e 411 Host Interface Manual v2.2 (November 2013) | ASTM E1381/E1394 |
> | Roche cobas c 111 | cobas c 111 Host Interface Manual v2.2 (June 2021, software 3.0 and higher) | ASTM E1381/E1394 |
> | Mindray BC-5000 | BC-5000 "3107 HL7 Communication Protocol" V2.0 | HL7 v2.3.1 over MLLP |
>
> Nothing in this repository may be called compatible with any instrument until the on-site Gate 2 verification
> (design 11.3) is signed for that model, software version and configuration.

## Layout

| Path | What it is |
| --- | --- |
| `src/Lims.Contracts` | Shapes shared between modules: normalized instrument messages, the proposed HMIS lab API records |
| `src/Lims.Platform` | Database access, SQL migrations, accession numbers, TOTP, retry schedule, roles |
| `src/Lims.Audit` | Hash-chained, append-only audit writer, chain verifier, external export with hourly anchors |
| `src/Lims.Messaging` | Inbox/outbox tables claimed with `FOR UPDATE SKIP LOCKED`, inbox processor, dead letter |
| `src/Lims.Instruments` | ASTM E1381 link and HL7/MLLP link, e411, c111 and BC-5000 parsers, query replies, order download, transports (TCP, serial), mappings |
| `src/Lims.Clinical` | Order registration (with panels), specimens, labels and recollection, result matching, review, approval, autoverification, corrections, downtime |
| `src/Lims.Hmis` | REST client (OAuth client credentials, rate limit), order poller, webhook events, outbox dispatcher with read-back |
| `src/Lims.Host` | ASP.NET Core host (Windows Service), API, local accounts with MFA, Razor Pages screens, health |
| `sim/Lims.Sim.E411` | cobas e 411 and c 111 simulator (plays the analyzer behind a serial device server) |
| `sim/Lims.Sim.Bc5000` | Mindray BC-5000 simulator (connects to the middleware over MLLP, like the analyzer) |
| `tools/Lims.Capture` | `lims-capture` site visit tool: listen-only or proxy capture, extract, anonymize, replay snapshot (see `tests/captures/README.md`) |
| `sim/Lims.Sim.Hmis` | HMIS simulator implementing the proposed lab API (design 6.9) with fault switches |
| `tests/` | Unit tests (no database) and end-to-end tests against PostgreSQL |
| `deploy/` | Windows install, backup and restore-drill scripts, PostgreSQL roles and grants |
| `installer/` | WiX v5 MSI (built on Windows CI) |
| `docs/IMPLEMENTATION-NOTES.md` | What Phase 1 covers, deviations from the design, and what is left |

Modules only reference `Contracts`, `Platform`, `Messaging` and `Audit`; an architecture test fails the build if
`Instruments`, `Clinical` or `Hmis` reference each other.

## Build and test

Requires the .NET 10 SDK and PostgreSQL 16 or later.

```bash
dotnet build
dotnet test tests/Lims.UnitTests                      # no database needed
LIMS_TEST_PG="Host=localhost;Username=lims_test;Password=lims_test;Database=postgres" \
  dotnet test tests/Lims.IntegrationTests             # creates and drops its own database
```

The integration tests start the real host, the HMIS simulator on Kestrel, and the e411 simulator over TCP, then
run the whole flow: HMIS order, poll, accession, host query, result upload, review, approval, HMIS delivery.
They also cover a lost HMIS response (read-back, no duplicate), HTTP 429 retry, duplicate and rerun messages,
unknown barcodes, unmapped codes, QC diversion, critical results, corrections sent as amendments, the database
refusing an unapproved outbox row, and audit tamper detection, plus the go-live operations: critical escalation,
the low-disk link refusal, catalog sync approval, exception resolve and the admin CLI. Unit tests cover secret
references, syslog over TLS against an in-process TLS server, and Kestrel HTTPS with a self-signed PFX.

The fixture also runs a cobas c 111 (ASTM, host query and order download) and a BC-5000 (HL7, the analyzer connects
to the middleware) next to the e411. Those tests cover the c111 query, results and QC; a CBC panel ordered once in
the HMIS, run on the BC-5000 and delivered per parameter on the same HMIS line; panel cancellation; a paused link;
autoverification with and without QC; recollection; catalog and mapping rollback; the signed audit export and its
tamper check; and a sign-in that renders every screen.

## Run locally with the simulators

```bash
# 1. HMIS simulator on :5080
dotnet run --project sim/Lims.Sim.Hmis

# 2. e411 simulator listening on :4001 (it waits for the middleware to connect, like a serial device server)
dotnet run --project sim/Lims.Sim.E411 -- --port 4001 query 261102000427

# 3. Middleware (put secrets in src/Lims.Host/appsettings.Local.json, which git ignores)
dotnet run --project src/Lims.Host

# Optional: BC-5000 simulator, connecting to the middleware's MLLP port 5100 (Transport.Mode TcpServer)
dotnet run --project sim/Lims.Sim.Bc5000 -- --port 5100 result 261103000101
```

Example `appsettings.Local.json`:

```json
{
  "ConnectionStrings": { "Lims": "Host=localhost;Database=lims;Username=lims_app;Password=..." },
  "Bootstrap": { "AdminUsername": "admin", "AdminPassword": "change-me-at-least-12" },
  "Hmis": { "ClientSecret": "sim-secret", "WebhookSecret": "sim-webhook-secret" },
  "Instruments": { "Instruments": [
    { "Id": "e411-1", "Parser": "roche.cobas-e411.astm", "Transport": { "Mode": "TcpClient", "Host": "127.0.0.1", "Port": 4001 } },
    { "Id": "c111-1", "Parser": "roche.cobas-c111.astm", "Model": "cobas c 111", "Transport": { "Mode": "TcpClient", "Host": "127.0.0.1", "Port": 4002 } },
    { "Id": "bc5000-1", "Parser": "mindray.bc5000.hl7", "Model": "Mindray BC-5000", "Transport": { "Mode": "TcpServer", "Host": "0.0.0.0", "Port": 5100 } }
  ] },
  "AuditExport": { "SigningKey": "dpapi-machine:..." },
  "Backup": { "Directory": "E:\\LimsBackup" }
}
```

Seed the catalog and mappings through the API as a Lab Manager (`POST /api/catalog`, `POST /api/mappings`, then
`POST /api/mappings/{id}/approve` by a second Lab Manager).

## Deploying on Windows

1. Install with `msiexec /i LimsMiddleware-<version>-x64.msi DATADIR=D:\LimsMiddleware CERTTHUMBPRINT=<thumbprint>`.
   The service is registered under `NT SERVICE\LimsMiddleware` and left stopped.
2. Create the database roles (`deploy/sql/roles.sql`), then `Lims.Host.exe migrate --connection "<owner connection>"` and `deploy/sql/grants.sql`.
3. Protect each secret on the server: `echo <secret> | "C:\Program Files\LimsMiddleware\Lims.Host.exe" protect-secret`,
   and put the printed `dpapi-machine:...` values in `appsettings.Local.json` in the install folder.
4. Run `deploy/windows/verify.ps1`, start the service, schedule `backup.ps1` (see `docs/BACKUP-RUNBOOK.md`).

## Key rules the code enforces

* A transport ACK is sent only after the frame (and, for the last frame, the whole message) is committed to PostgreSQL.
* Results match on accession barcode only. Unknown barcodes, unmapped codes, unit mismatches, tests not ordered on
  the tube, and QC records carrying a patient barcode go to exceptions, never to a patient result.
* No result leaves without an approval: the database rejects an outbox row with no matching approval review.
* Every HMIS POST carries an `Idempotency-Key` derived from result id and version. A timeout is treated as
  "unknown" and followed by read-back before any resend.
* Corrections are new versions and go to the HMIS as amendments that reference the original HMIS result id.
* Every state change writes a hash-chained audit event in the same transaction; UPDATE and DELETE on the audit
  table are refused by a trigger.
* Nothing is autoverified at install. A rule needs a validation document ID and a second Lab Manager, and releases a
  result only when every condition holds (first run, numeric, in limits, no flags, delta passed, unflagged QC in the window).
