# Captured analyzer traffic

Real messages recorded at the site visit, with patient identity removed, used as parser regression tests.
One folder per parser id: `roche.cobas-e411.astm`, `roche.cobas-c111.astm`, `mindray.bc5000.hl7`. An analyzer registered
on the Analyzer setup screen gets a folder named after its analyzer ID holding its approved definition as `definition.json`
(the `definition` of its entry in `GET /api/analyzers`); its messages replay with that layout.

Nothing here yet: the folders fill during the Gate 2 site visit (design 11.3).

## From capture to test

```bash
# On site, acknowledge-only (no orders, no query replies, nothing filed). Ctrl+C to stop.
lims-capture listen --protocol astm --tcp-connect 10.20.0.15:4001 --out capture-e411       # via the serial device server
lims-capture listen --protocol hl7  --tcp-listen 0.0.0.0:5100   --out capture-bc5000      # the BC-5000 connects to us
# or, beside a host system that already works, passively:
lims-capture proxy --listen 0.0.0.0:5100 --upstream 10.20.0.40:5100 --out capture-bc5000
lims-capture extract capture-bc5000 --protocol hl7                                         # writes capture-bc5000/extracted

# Before anything leaves the site
lims-capture anonymize capture-e411/messages tests/captures/roche.cobas-e411.astm --protocol astm
#   then open every file and check for names, phone numbers or free text the tool does not know about.

# Record what the parser makes of each message, review the JSON against the analyzer screen, commit both
lims-capture snapshot tests/captures/roche.cobas-e411.astm --parser roche.cobas-e411.astm
lims-capture snapshot tests/captures/sysmex-xn-1                 # registered analyzer: uses its definition.json
```

A registered analyzer's snapshots belong to one definition version. When a new version is approved, update
`definition.json`, run `snapshot` again and review the differences.

`capture.jsonl` holds every byte with timestamps and is **not anonymized**: it stays on the site laptop and is deleted
after the visit (or kept under the hospital's records policy). Only the anonymized `.msg` files are committed.
