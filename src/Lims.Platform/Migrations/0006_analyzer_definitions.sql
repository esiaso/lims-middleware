-- Analyzers registered from the screens (no built-in adapter): versioned definitions with maker-checker approval.
-- A definition holds the model, transport and message layout (generic ASTM or HL7 adapter) as JSON. A Lab Manager
-- proposes it; a different Lab Manager approves it, and only then does the link start. One active version per analyzer.
CREATE TABLE instruments.analyzer_definition (
    id              uuid PRIMARY KEY,
    instrument_id   text NOT NULL CHECK (instrument_id ~ '^[a-z0-9][a-z0-9._-]{0,39}$'),
    version         integer NOT NULL,
    model           text NOT NULL CHECK (length(btrim(model)) > 0),
    protocol        text NOT NULL CHECK (protocol IN ('astm','hl7')),
    definition      jsonb NOT NULL,
    enabled         boolean NOT NULL DEFAULT true,
    status          text NOT NULL CHECK (status IN ('proposed','active','retired')),
    proposed_by     text NOT NULL,
    proposed_at     timestamptz NOT NULL DEFAULT now(),
    approved_by     text NULL,
    approved_at     timestamptz NULL,
    disabled_by     text NULL,
    disabled_at     timestamptz NULL,
    disable_reason  text NULL,
    UNIQUE (instrument_id, version),
    CHECK (approved_by IS NULL OR approved_by <> proposed_by),
    CHECK (enabled OR disable_reason IS NOT NULL)
);
CREATE UNIQUE INDEX ux_analyzer_definition_active ON instruments.analyzer_definition (instrument_id) WHERE status = 'active';
CREATE UNIQUE INDEX ux_analyzer_definition_proposed ON instruments.analyzer_definition (instrument_id) WHERE status = 'proposed';
