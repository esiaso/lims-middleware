# Phase 1 implementation notes

Status as of this branch. Section numbers refer to the design document.

## Built

| Design item | Where | Notes |
| --- | --- | --- |
| Modular monolith, .NET 10, Windows Service (3) | `src/*`, `LimsApp.cs` | `UseWindowsService`; module boundaries checked by `ArchitectureTests` |
| PostgreSQL schema, inbox/outbox, dead letter (7.2) | `Platform/Migrations/0001_initial.sql` | Embedded SQL migrations, advisory lock, refuses to start against a newer schema |
| ASTM link state machine (6.3) | `Instruments/Astm/AstmLinkSession.cs` | Frame journal before ACK, message committed before last ACK, NAK when storage is down, duplicate frame handling, contention, timeouts |
| e411 parser, both "cobas type" and "Elecsys type" (4, 4.1) | `CobasE411Parser.cs` | Tested against the manual's section F traces |
| e411 host query reply (TSDWN^REPLY) | `CobasE411QueryReply.cs`, `E411QueryResponder` | Echoes sequence, carrier, position; max 18 tests; accepted specimens only |
| Transports (6.2) | `LinkTransports.cs` | TCP client (serial device server), TCP server, local COM port |
| Order intake by polling and webhook (6.7, 6.9) | `Hmis/OrderIntake.cs` | Cursor and orders commit together; webhook HMAC-SHA256 with 5 min skew; stored before 202 |
| Patient/encounter/order registration (7.1) | `Clinical/OrderRegistrationHandler.cs` | Upsert by HMIS ids; cancellation after release is audited, not applied |
| Accessioning and ZPL labels (5.1, E7) | `SpecimenService.cs`, `ZplLabel.cs` | YYMMDD + 5 digits + Luhn check digit; Code 128; print over TCP 9100; reprint audited |
| Matching, mapping, QC diversion, quarantine (5.5, 5.7, 6.6) | `InstrumentResultHandler.cs` | Fingerprint duplicates (7 days), reruns create new versions |
| Review, approval, critical results, corrections (7.8) | `ResultReviewService.cs` | Reviewer cannot approve own review (configurable); critical needs notification record |
| HMIS delivery with idempotency and read-back (7.4, 7.5) | `Hmis/OutboxDispatcher.cs` | Backoff schedule, Retry-After, circuit breaker, manual confirmation queue when read-back is unsupported |
| Downtime registration and linking (5.5) | `DowntimeService.cs`, `ResultReviewService.LinkDowntimeOrderAsync` | API only; results held until linked |
| Audit hash chain, verify, export (9.6) | `Audit/*` | Append-only trigger; external JSON-lines sink and hourly anchors |
| Local accounts + TOTP MFA, lockout (9.2) | `Host/Identity/UserStore.cs` | PBKDF2 (ASP.NET Core Identity hasher); MFA for approver, lab manager, admin, integration operator |
| Screens (8) | `Host/Pages` | Health, Orders (collect, accept), Review (review, approve, reject), Exceptions, Deliveries |
| Simulators (11.1) | `sim/*` | e411 with checksum/duplicate/abort faults; HMIS with 500, 429, slow, dropped-response faults |

## Deviations from the design (please review)

1. **Dapper and SQL migrations instead of EF Core.** The queue claims (`FOR UPDATE SKIP LOCKED`), triggers and
   hash chain are clearer as plain SQL, and there is one fewer layer to validate. Migrations stay versioned and
   expand-only. Easy to revisit if the team prefers EF Core.
2. **One result per HMIS POST.** The proposed API allows 50 per request; one per request keeps the idempotency key
   equal to one result version. Batching can come later if throughput needs it (about 1,000 tests a day does not).
3. **LOINC comes from the order line, else from the active catalog entry.**
4. **Accession check digit is Luhn mod 10.** The design left the algorithm open.

## Go-live items (PR #2)

* **Secrets (9.4):** `dpapi-machine:` / `dpapi-user:` values made with `Lims.Host.exe protect-secret`, `credential:` (systemd) and
  `file:` references. Outside Development and Testing the service refuses to start if a secret key holds a plain value
  (override: `AllowPlaintextSecrets=true`, for emergencies only).
* **HTTPS (9.4):** Kestrel binds only HTTPS when `Https:CertificateThumbprint` (LocalMachine\My) or a PFX is set; TLS 1.2 and 1.3;
  refuses to start with an expired certificate. HSTS is left to the reverse proxy if one is added.
* **Reconciliation (7.7) and critical escalation (7.8):** every 5 minutes; delivery overdue (STAT 15 min, routine 60 min),
  critical not acknowledged (15 min, audited as `result.critical_escalated`), pending at instrument past the test's TAT,
  orders not collected in 24 h, HMIS cancels after release, unlinked downtime orders. One open item per problem; items
  close themselves when the condition clears, except cancel-after-release, which needs an approver.
* **Disk watch (7.6):** warn at 20% free, critical at 10%, and at 5% the ASTM link NAKs new messages so the analyzer keeps them.
* **Audit export:** syslog over TLS (RFC 5424 messages, RFC 5425 framing, optional certificate pin) alongside the WORM file sink, each with its own position.
* **Catalog sync:** daily `GET /test-catalog`; changes become proposed catalog versions that a Lab Manager approves (not the proposer).
* **Screens:** configuration (instruments, mappings, catalog approvals, certificate), audit log search and chain verify (Auditor),
  downtime registration and linking, reconciliation, exception resolve with reason.
* **Delta checks:** per test, a percent and/or absolute limit and a window (default 30 days) in the catalog. A numeric
  result that moves more than the limit from the patient's latest released result is flagged (`result.delta_flagged`),
  shown on the review screen, and needs a review comment or the approver's confirmation before approval.
  No limits are set by default: the Lab Manager enters them per test on the configuration screen.
* **Catalog editing:** the configuration screen proposes a new catalog version (ranges, critical and delta limits) for a
  second Lab Manager to approve, alongside the existing mapping proposal form.
* **Backup (10.4):** `deploy/windows/backup.ps1`, `restore-test.ps1` and `docs/BACKUP-RUNBOOK.md` (WAL archiving for the 15-minute RPO).
* **MSI (10.2):** WiX v5 project in `installer/`, built and install-tested on a Windows CI runner. It is **not code-signed**
  yet; signing needs the hospital's or vendor's code-signing certificate, added as a CI secret.

## Full application (PR #3)

* **cobas c 111 adapter:** ASTM parser and TSDWN^REPLY query reply from the c111 Host Interface Manual v2.2. Sample ID is read
  from O-4 ("SampleID^^Position"); QC is action code Q with sample ID 1000 + control code; result status X is stored empty with
  "not completed", R (re-transmission) is treated as F so duplicates are caught, I on QC is dropped; C records become alarm flags;
  calibration uploads are not results. A query for an unknown sample is answered with report type Z.
* **Mindray BC-5000 adapter:** HL7 v2.3.1 over MLLP (UTF-8). Each message is committed to the inbox before ACK^R01; storage
  trouble or a paused link is answered AE with error 207 so the analyzer keeps the result. Run information, histograms and
  scattergrams are not results; IS items with value T become alerts on every parameter of the sample; "*" parameters get an
  RUO flag. Worklist queries (ORM^O01) are answered with ORR^O02 and Test Mode CBC or CBC+DIFF (DIFF when any differential
  parameter is ordered). In production the analyzer connects to the middleware (`Transport.Mode = TcpServer`).
* **Test panels:** a catalog entry can list panel components. An HMIS line for a panel (for example CBC) becomes one order test per
  component, all with the HMIS line id; each component is resulted, approved and delivered separately on that line with its own
  LOINC. Cancelling the line cancels every component. Panels are versioned and approved like any catalog change.
* **Not reportable mappings:** an instrument code mapped as not reportable (for example BC-5000 research parameters the lab does
  not report) is kept for traceability and closed at once instead of opening an exception.
* **Screens (design 8):**
  * Integration health: a tile per link, HMIS, disk and last backup, queue counts linked to their screens, audit export lag,
    and pause/resume per link (Integration operator, with a reason, audited). Pausing is held in memory, so a restart resumes all links.
  * Analyzers and worklists: link state and verification status, pending worklist, printable load list (initials and year of
    birth, audited), last messages as metadata only, QC received today, and "resend order" (c111 and e411 cobas type use the
    manuals' TSDWN^BATCH download; the BC-5000 pulls its worklist when it reads the tube, so the screen says to rescan).
  * Specimens: search, timeline from the audit trail, accept, reject with reason, request recollection (the next collection is
    linked to the old tube), reprint label.
  * Configuration: history of catalog and mapping versions, and roll back to a retired version (proposed again, needs a second
    Lab Manager); panel components in the catalog form; autoverification rules.
  * Audit: instrument and action-type filters; export of every matching event as CSV or JSON in a ZIP with a manifest and an
    HMAC-SHA256 signature (`AuditExport:SigningKey`), checked with `Lims.Host.exe verify-export FILE.zip --key <secret ref>`.
* **Autoverification (5.6):** off at install. A Lab Manager proposes a rule per test (optional instrument, limits or the reference
  range, QC window, validation study document ID); a different Lab Manager enables it; disabling is immediate. A result is released
  by the rule only when it is the first run, numeric with no comparator, not critical, no flag mismatch, delta check passed, no flags
  other than N, inside the limits, and the latest QC for that instrument and test inside the window is unflagged. The approval names
  the rule version (`autoverify:GLU:v1`) and links the rule; skipped results are audited with the reasons. `Autoverification:Suspended`
  stops all rules at once. The middleware does not run Westgard rules: the QC condition only checks that QC ran and the analyzer did not flag it.
* **Fix:** Razor pages are now found when the host is started from another entry point (the integration tests); a test signs in
  and renders every screen.

**Upgrading an existing install:** stop the service, run `Lims.Host.exe migrate` (adds migrations 0004 panels and
0005 autoverification), run `deploy/sql/grants.sql` again so `lims_app` gets the new table, then start the service.

## Offline install bundle (PR #5)

* `deploy/bundle/build-bundle.ps1` (run by the `windows-msi` CI job) packs the MSI, the EDB PostgreSQL 17 installer
  (hash pinned in `deploy/bundle/prereqs.json`, Authenticode signature checked), the `lims-capture` tool, the deploy
  scripts and docs, with `SHA256SUMS.txt` and `bundle.json`. No .NET runtime is included because the service is self-contained.
* `Install-Offline.ps1` checks every hash, installs PostgreSQL (localhost only), the MSI, runs `configure.ps1`, starts the
  service and runs `verify.ps1`. CI installs the bundle end to end on Windows Server 2022 with Windows PowerShell 5.1,
  checks that a modified bundle is refused, and runs `configure.ps1` a second time.
* `deploy/windows/configure.ps1` was referenced by `install.ps1` and `roles.sql` but did not exist; it does now.
* Fixed while testing the install: the service could not start as `lims_app`, because the startup migration check ran
  `CREATE TABLE IF NOT EXISTS` (which needs the CREATE right) and `lims_app` had no USAGE on schema `public`.
  The check now looks before creating, `grants.sql` grants USAGE on `public`, and with pending migrations the service
  says to run `migrate` as `lims_owner` (`DeploymentRolesTests`).

## Still not built

* Code signing is wired into CI (`docs/CODE-SIGNING.md`) but the build stays unsigned until a certificate is added as a secret.
* PowerShell scripts were written for Windows Server 2022 but have only been syntax-reviewed, not run, because no Windows host was available; the MSI is exercised by CI.

## Open items that block Gate 2

For each analyzer (e411, c111, BC-5000), at the site visit:

* Installed software version (and for the e411, whether the cobas or Elecsys protocol variant is configured; design 4.1).
* Real traffic captured in listen-only mode and replayed through the parser tests. Use `tools/Lims.Capture`
  (`lims-capture listen|proxy|extract|anonymize|snapshot`); anonymized messages go in `tests/captures/<parser id>/`
  and `CaptureReplayTests` checks them against their reviewed `.json` snapshots. The step by step checklist for the
  c111 and BC-5000 visit is a separate document.
* For the c111: whether host query or batch download is configured, and the test codes in use (the manual's numbers are examples).
* For the BC-5000: the LIS port and connection direction, the exact "no order" worklist reply and accepted Test Mode values (to be
  confirmed with Mindray), and which parameters the lab reports.

Also:

* e411 and c111: cable pinout confirmed on site; serial device server passes the configured handshake.
* Agreement with the HMIS team on the lab API in 6.9 (the simulator follows the proposal, not an agreed contract).
