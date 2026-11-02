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
* **Backup (10.4):** `deploy/windows/backup.ps1`, `restore-test.ps1` and `docs/BACKUP-RUNBOOK.md` (WAL archiving for the 15-minute RPO).
* **MSI (10.2):** WiX v5 project in `installer/`, built and install-tested on a Windows CI runner. It is **not code-signed**
  yet; signing needs the hospital's or vendor's code-signing certificate, added as a CI secret.

## Still not built

* Code signing of the MSI and an offline bundle with PostgreSQL and the .NET prerequisites.
* Delta checks against the patient's previous result.
* Dedicated mapping editor screen (mappings are proposed and approved through the API and the configuration screen lists them).
* cobas c111 and Mindray BC-5000 adapters (later phases).
* PowerShell scripts were written for Windows Server 2022 but have only been syntax-reviewed, not run, because no Windows host was available; the MSI is exercised by CI.

## Open items that block Gate 2 for the e411

* Installed software version and whether the cobas or Elecsys protocol variant is configured (4.1).
* Real traffic captured in listen-only mode, replayed through the parser tests.
* Cable pinout confirmed on site; serial device server passes the configured handshake.
* Agreement with the HMIS team on the lab API in 6.9 (the simulator follows the proposal, not an agreed contract).
