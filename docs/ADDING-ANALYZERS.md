# Adding an analyzer that has no built-in adapter

The built-in adapters (cobas e 411, cobas c 111, Mindray BC-5000) are written from each vendor's manual and set up in
`appsettings`. Any other analyzer that talks **ASTM E1381/E1394** or **HL7 v2 over MLLP**, over TCP or a serial port,
can be registered from the **Analyzer setup** screen without a code change or a service restart. It runs on the generic
ASTM or HL7 adapter, which reads the message layout a Lab Manager describes from the analyzer's host interface manual.

> A registered analyzer is **CONFIGURED, not interface-tested.** It is recorded as `unverified` and stays so until the
> on-site verification (design 11.3) is signed for that model, software version and configuration. Any later approved
> change to a verified analyzer sets it to `re-verify`.

## Steps

1. **Capture real traffic first.** Use `lims-capture listen` (see `tests/captures/README.md`) to record a few results, a
   QC run and, if the analyzer uses host query, a query. Anonymize before taking the files off site.
2. **Describe the analyzer** on *Analyzer setup* (Lab Manager):
   * Identity: analyzer ID (lower case, for example `sysmex-xn-1`), model, software version, clock offset from UTC.
   * Protocol: ASTM or HL7. Worklist: `None` (results only, load list), `HostQuery` (the analyzer asks for a tube's tests),
     `Push` ("Resend order" sends the order), or `Both`.
   * Connection: `TcpClient` (the middleware connects to the analyzer or its serial device server), `TcpServer` (the
     analyzer connects to the middleware; bind address and port), or `Serial` (COM port, baud, data bits, parity, stop
     bits, handshake). A port already used by another analyzer is refused.
   * Message layout: where the sample ID, test code, value, units, reference range, abnormal flag, result status,
     operator and completion time are. Positions are written `record-field.component`, counted from 1 as in the
     manuals: `R-4`, `R-3.4`, `OBX-3.1`. Defaults follow E1394 and HL7 2.3.1 as most analyzers use them. Also: codes that
     are not results (run modes, histograms), QC detection (ASTM action code `Q` in O-12, HL7 MSH-11 `Q`, or sample ID
     prefixes), result status values that mean rerun, timestamp formats, and the names put in the middleware's headers.
3. **Check** with a captured message pasted into the form. The screen shows how it is read (sample, codes, values, flags,
   QC or patient) and saves nothing. Adjust positions until it is right.
4. **Propose.** A different Lab Manager reviews and **approves** it on the same screen. The link starts within seconds.
   Every step (proposed, approved, disabled, withdrawn, rollback proposed) is in the audit log.
5. **Map the tests** on *Configuration* (instrument code to catalog code, approved by a second Lab Manager), exactly as
   for the built-in analyzers. Until a code is mapped, its results go to Exceptions, never to a patient.

## Changing, disabling, rolling back

* *Edit as new version* pre-fills the form from any version; the proposal replaces the active version only when approved,
  and the link restarts on the new settings.
* *Disable* (with a reason) stops the link at once. The analyzer keeps its unsent results. Enabling it again is a new
  proposal that needs approval, as is *Roll back to this* on a retired version.
* Analyzers set up in `appsettings` are listed but cannot be changed here, and their IDs cannot be registered.

## What the generic adapters do and do not do

* ASTM: the E1381 link layer is the same as for the built-in adapters (frames journaled before ACK, the message committed
  before the last ACK, contention, NAK on storage trouble). Records H, P, O, R, C, Q, L are read; M and S are ignored.
  The host query reply and the pushed order are one O record per sample (O-3 sample, O-5 tests from the template
  `^^^{code}`, O-6 priority, O-12 `A`); with no order, the reply is H and L only.
* HL7: every message is committed before the ACK (`AE` 207 on a commit failure, as for the BC-5000). OBX segments under
  OBR are results; ED/RP values (images, histograms) are skipped. The order query reply (default `ORR^O02`) and the
  pushed order (default `ORM^O01`) carry PID, ORC and one OBR per test with the instrument code in OBR-4. The analyzer's
  ACK to a pushed order is logged, not stored.
* The reply layouts are generic. Many analyzers expect vendor-specific fields in their replies; confirm with the vendor
  and the captures. An analyzer whose replies cannot be expressed this way needs a built-in adapter.
* `lims-capture snapshot` replays captures with the generic adapters' default layout only, not a registered profile.

## API

Lab Managers can do the same over the API: `GET /api/analyzers[?history=true]`, `POST /api/analyzers` (definition
JSON, as returned by the GET), `POST /api/analyzers/{definitionId}/approve`, `POST /api/analyzers/{analyzerId}/disable`
with `{"reason": "..."}`.
