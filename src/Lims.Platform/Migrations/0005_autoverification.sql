-- Autoverification (design 5.6): off at install. A rule for a test is proposed by a Lab Manager with the ID of the
-- validation study document and takes effect only when a different Lab Manager approves it.
CREATE TABLE clinical.autoverify_rule (
    id                      uuid PRIMARY KEY,
    catalog_code            text NOT NULL,
    version                 integer NOT NULL,
    instrument_id           text NULL,                 -- NULL: results from any instrument
    limit_low               numeric NULL,              -- both NULL: the catalog reference range
    limit_high              numeric NULL,
    qc_window_hours         integer NOT NULL DEFAULT 24 CHECK (qc_window_hours BETWEEN 1 AND 168),
    validation_document_id  text NOT NULL CHECK (length(btrim(validation_document_id)) > 0),
    status                  text NOT NULL CHECK (status IN ('proposed','active','retired')),
    proposed_by             text NOT NULL,
    proposed_at             timestamptz NOT NULL DEFAULT now(),
    approved_by             text NULL,
    approved_at             timestamptz NULL,
    retired_by              text NULL,
    retired_at              timestamptz NULL,
    retire_reason           text NULL,
    UNIQUE (catalog_code, version),
    CHECK (approved_by IS NULL OR approved_by <> proposed_by),
    CHECK (limit_low IS NULL OR limit_high IS NULL OR limit_low <= limit_high)
);
CREATE UNIQUE INDEX ux_autoverify_active ON clinical.autoverify_rule (catalog_code) WHERE status = 'active';

-- An autoverified approval names the rule version that released it.
ALTER TABLE clinical.review ADD COLUMN autoverify_rule_id uuid NULL REFERENCES clinical.autoverify_rule(id);
