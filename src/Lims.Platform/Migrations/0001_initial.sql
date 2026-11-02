-- 0001_initial: schemas, inbox/outbox, link journal, clinical core, hash-chained audit.
-- Expand/contract rule (design 10.6): later migrations only add; destructive changes wait one release.

CREATE SCHEMA IF NOT EXISTS platform;
CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS messaging;
CREATE SCHEMA IF NOT EXISTS instruments;
CREATE SCHEMA IF NOT EXISTS clinical;
CREATE SCHEMA IF NOT EXISTS hmis;

-- ---------------------------------------------------------------- platform
CREATE TABLE platform.user_account (
    id              uuid PRIMARY KEY,
    username        text NOT NULL UNIQUE,
    display_name    text NOT NULL,
    password_hash   text NOT NULL,
    roles           text[] NOT NULL DEFAULT '{}',
    totp_secret     text NULL,          -- base32; protected by disk encryption + DB access control
    mfa_enrolled    boolean NOT NULL DEFAULT false,
    failed_logins   integer NOT NULL DEFAULT 0,
    locked_until    timestamptz NULL,
    is_active       boolean NOT NULL DEFAULT true,
    created_at      timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------- audit
CREATE TABLE audit.audit_event (
    seq             bigint PRIMARY KEY,
    occurred_at     timestamptz NOT NULL,
    actor           text NOT NULL,
    actor_address   text NULL,
    action          text NOT NULL,
    entity_type     text NOT NULL,
    entity_id       text NULL,
    accession       text NULL,
    patient_id      text NULL,
    correlation_id  text NULL,
    reason          text NULL,
    before_json     jsonb NULL,
    after_json      jsonb NULL,
    prev_hash       bytea NOT NULL,
    hash            bytea NOT NULL
);
CREATE INDEX ix_audit_entity ON audit.audit_event (entity_type, entity_id);
CREATE INDEX ix_audit_accession ON audit.audit_event (accession) WHERE accession IS NOT NULL;
CREATE INDEX ix_audit_correlation ON audit.audit_event (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX ix_audit_time ON audit.audit_event (occurred_at);

CREATE FUNCTION audit.reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit.audit_event is append-only (% rejected)', TG_OP;
END $$;
CREATE TRIGGER trg_audit_append_only BEFORE UPDATE OR DELETE ON audit.audit_event
    FOR EACH ROW EXECUTE FUNCTION audit.reject_change();
CREATE TRIGGER trg_audit_no_truncate BEFORE TRUNCATE ON audit.audit_event
    FOR EACH STATEMENT EXECUTE FUNCTION audit.reject_change();

-- External anchors: chain head written out every hour (design 9.6).
CREATE TABLE audit.chain_anchor (
    id          bigserial PRIMARY KEY,
    seq         bigint NOT NULL,
    hash        bytea NOT NULL,
    written_at  timestamptz NOT NULL DEFAULT now(),
    destination text NOT NULL
);

CREATE TABLE audit.export_state (
    destination text PRIMARY KEY,
    last_seq    bigint NOT NULL DEFAULT 0,
    updated_at  timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------- messaging
CREATE TABLE messaging.inbox_message (
    id              uuid PRIMARY KEY,
    source          text NOT NULL,              -- instrument | hmis
    channel         text NOT NULL,              -- instrument id, or 'hmis'
    kind            text NOT NULL,
    raw             bytea NOT NULL,
    raw_sha256      bytea NOT NULL,
    dedupe_key      text NULL,                  -- e.g. HMIS order id + updated_at
    received_at     timestamptz NOT NULL DEFAULT now(),
    correlation_id  text NOT NULL,
    status          text NOT NULL DEFAULT 'pending'
                    CHECK (status IN ('pending','processed','duplicate','quarantined','failed')),
    attempts        integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    locked_until    timestamptz NULL,
    last_error      text NULL,
    processed_at    timestamptz NULL
);
CREATE UNIQUE INDEX ux_inbox_dedupe ON messaging.inbox_message (source, kind, dedupe_key) WHERE dedupe_key IS NOT NULL;
CREATE INDEX ix_inbox_claim ON messaging.inbox_message (next_attempt_at) WHERE status = 'pending';

CREATE TABLE messaging.outbox_message (
    id              uuid PRIMARY KEY,
    destination     text NOT NULL,              -- hmis
    kind            text NOT NULL,
    aggregate_id    uuid NOT NULL,              -- result version id
    approval_id     uuid NULL,                  -- clinical.review id that released it
    payload         jsonb NOT NULL,
    idempotency_key text NOT NULL UNIQUE,
    correlation_id  text NOT NULL,
    priority        integer NOT NULL DEFAULT 0, -- higher first (stat)
    status          text NOT NULL DEFAULT 'pending'
                    CHECK (status IN ('pending','sent_unknown','confirmed','dead','manual_confirmation')),
    attempts        integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    locked_until    timestamptz NULL,
    last_error      text NULL,
    external_ref    text NULL,                  -- HMIS result id once confirmed
    created_at      timestamptz NOT NULL DEFAULT now(),
    confirmed_at    timestamptz NULL
);
CREATE INDEX ix_outbox_claim ON messaging.outbox_message (priority DESC, next_attempt_at) WHERE status IN ('pending','sent_unknown');
CREATE INDEX ix_outbox_aggregate ON messaging.outbox_message (aggregate_id);

CREATE TABLE messaging.dead_letter (
    id              uuid PRIMARY KEY,
    origin          text NOT NULL CHECK (origin IN ('inbox','outbox')),
    origin_id       uuid NOT NULL,
    reason          text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    resolved_at     timestamptz NULL,
    resolved_by     text NULL,
    resolution      text NULL
);
CREATE INDEX ix_dead_letter_open ON messaging.dead_letter (created_at) WHERE resolved_at IS NULL;

-- ---------------------------------------------------------------- instruments
CREATE TABLE instruments.instrument (
    id                  text PRIMARY KEY,
    model               text NOT NULL,
    protocol            text NOT NULL,
    software_version    text NULL,
    verification_status text NOT NULL DEFAULT 'unverified'
                        CHECK (verification_status IN ('unverified','documented','verified','re-verify')),
    created_at          timestamptz NOT NULL DEFAULT now()
);

-- Per-frame journal: every ASTM frame is written here before the ACK goes out (design 7.2).
CREATE TABLE instruments.link_frame (
    id              bigserial PRIMARY KEY,
    instrument_id   text NOT NULL,
    session_id      uuid NOT NULL,
    frame_index     integer NOT NULL,
    frame_number    smallint NOT NULL,
    text            bytea NOT NULL,
    is_final        boolean NOT NULL,
    received_at     timestamptz NOT NULL DEFAULT now(),
    status          text NOT NULL DEFAULT 'open' CHECK (status IN ('open','assembled','incomplete')),
    inbox_message_id uuid NULL
);
CREATE INDEX ix_link_frame_open ON instruments.link_frame (instrument_id, session_id) WHERE status = 'open';

-- Raw bytes the middleware sent to an instrument (query replies, order downloads).
CREATE TABLE instruments.outbound_message (
    id              uuid PRIMARY KEY,
    instrument_id   text NOT NULL,
    kind            text NOT NULL,
    raw             bytea NOT NULL,
    correlation_id  text NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    sent_at         timestamptz NULL,
    outcome         text NULL
);

-- Versioned instrument-code mapping (design 6.6). Only one active version per code.
CREATE TABLE instruments.test_mapping (
    id                  uuid PRIMARY KEY,
    instrument_id       text NOT NULL REFERENCES instruments.instrument(id),
    instrument_code     text NOT NULL,
    catalog_code        text NOT NULL,
    version             integer NOT NULL,
    result_type         text NOT NULL DEFAULT 'numeric' CHECK (result_type IN ('numeric','qualitative','text')),
    unit_factor         numeric NOT NULL DEFAULT 1,
    instrument_unit     text NULL,          -- expected unit from the analyzer; a different unit is quarantined
    reportable          boolean NOT NULL DEFAULT true,
    status              text NOT NULL DEFAULT 'proposed' CHECK (status IN ('proposed','active','retired')),
    proposed_by         text NOT NULL,
    proposed_at         timestamptz NOT NULL DEFAULT now(),
    approved_by         text NULL,
    approved_at         timestamptz NULL,
    UNIQUE (instrument_id, instrument_code, version),
    CHECK (approved_by IS NULL OR approved_by <> proposed_by)
);
CREATE UNIQUE INDEX ux_mapping_active ON instruments.test_mapping (instrument_id, instrument_code) WHERE status = 'active';

-- ---------------------------------------------------------------- clinical
CREATE TABLE clinical.test_catalog (
    code                text NOT NULL,
    version             integer NOT NULL,
    name                text NOT NULL,
    loinc               text NULL,
    ucum_unit           text NULL,
    decimals            integer NOT NULL DEFAULT 2,
    ref_low             numeric NULL,
    ref_high            numeric NULL,
    critical_low        numeric NULL,
    critical_high       numeric NULL,
    specimen_type       text NOT NULL DEFAULT 'serum',
    default_instrument  text NULL,
    status              text NOT NULL DEFAULT 'active' CHECK (status IN ('proposed','active','retired')),
    approved_by         text NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (code, version)
);
CREATE UNIQUE INDEX ux_catalog_active ON clinical.test_catalog (code) WHERE status = 'active';

CREATE TABLE clinical.patient (
    id              uuid PRIMARY KEY,
    mrn             text NOT NULL UNIQUE,
    given_name      text NULL,
    family_name     text NULL,
    sex             text NULL,
    birth_date      date NULL,
    national_id     text NULL,
    sha_number      text NULL,
    last_synced_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE clinical.encounter (
    id                  uuid PRIMARY KEY,
    hmis_encounter_id   text NOT NULL UNIQUE,
    patient_id          uuid NOT NULL REFERENCES clinical.patient(id),
    type                text NULL,
    location            text NULL
);

CREATE TABLE clinical.lab_order (
    id                  uuid PRIMARY KEY,
    hmis_order_id       text NOT NULL UNIQUE,      -- 'DT-...' for downtime orders
    encounter_id        uuid NULL REFERENCES clinical.encounter(id),
    patient_id          uuid NOT NULL REFERENCES clinical.patient(id),
    priority            text NOT NULL DEFAULT 'routine',
    ordering_clinician  text NULL,
    status              text NOT NULL DEFAULT 'registered'
                        CHECK (status IN ('registered','in_progress','completed','cancelled')),
    hmis_updated_at     timestamptz NULL,
    is_downtime         boolean NOT NULL DEFAULT false,
    linked_hmis_order_id text NULL,
    created_at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE clinical.order_test (
    id                  uuid PRIMARY KEY,
    order_id            uuid NOT NULL REFERENCES clinical.lab_order(id),
    hmis_line_id        text NOT NULL,
    catalog_code        text NOT NULL,
    loinc               text NULL,
    status              text NOT NULL DEFAULT 'ordered'
                        CHECK (status IN ('ordered','pending_specimen','pending_at_instrument','resulted','released','cancelled','not_performed')),
    UNIQUE (order_id, hmis_line_id)
);

CREATE TABLE clinical.accession_counter (
    day     date PRIMARY KEY,
    last    integer NOT NULL
);

CREATE TABLE clinical.specimen (
    id                  uuid PRIMARY KEY,
    accession           text NOT NULL UNIQUE,      -- display form 26110200042-7
    barcode             text NOT NULL UNIQUE,      -- what is on the label and sent by analyzers
    order_id            uuid NOT NULL REFERENCES clinical.lab_order(id),
    specimen_type       text NOT NULL,
    status              text NOT NULL DEFAULT 'collected'
                        CHECK (status IN ('expected','collected','received','accepted','rejected','recollection_requested','on_instrument','testing_complete','stored','disposed')),
    collected_at        timestamptz NULL,
    collected_by        text NULL,
    received_at         timestamptz NULL,
    received_by         text NULL,
    rejection_reason    text NULL,
    recollection_of     uuid NULL REFERENCES clinical.specimen(id),
    created_at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE clinical.specimen_test (
    id              uuid PRIMARY KEY,
    specimen_id     uuid NOT NULL REFERENCES clinical.specimen(id),
    order_test_id   uuid NOT NULL REFERENCES clinical.order_test(id),
    instrument_id   text NULL,
    UNIQUE (specimen_id, order_test_id)
);

-- One row per parsed instrument result (immutable, design 5.2).
CREATE TABLE clinical.instrument_result (
    id                  uuid PRIMARY KEY,
    inbox_message_id    uuid NOT NULL REFERENCES messaging.inbox_message(id),
    instrument_id       text NOT NULL,
    sample_id           text NULL,
    instrument_test_code text NOT NULL,
    raw_value           text NOT NULL,
    units               text NULL,
    comparator          text NULL,
    numeric_value       numeric NULL,
    qualitative_value   text NULL,
    secondary_value     text NULL,
    instrument_flags    text[] NOT NULL DEFAULT '{}',
    reference_range_text text NULL,
    run_at              timestamptz NULL,
    dilution            text NULL,
    is_rerun            boolean NOT NULL DEFAULT false,
    fingerprint         text NOT NULL,
    disposition         text NOT NULL CHECK (disposition IN ('matched','unmatched','quarantined','duplicate')),
    quarantine_reason   text NULL,
    specimen_test_id    uuid NULL REFERENCES clinical.specimen_test(id),
    mapping_id          uuid NULL,
    correlation_id      text NOT NULL,
    received_at         timestamptz NOT NULL DEFAULT now(),
    resolved_at         timestamptz NULL,
    resolved_by         text NULL
);
CREATE INDEX ix_ir_fingerprint ON clinical.instrument_result (instrument_id, fingerprint);
CREATE INDEX ix_ir_open_exceptions ON clinical.instrument_result (received_at) WHERE disposition IN ('unmatched','quarantined') AND resolved_at IS NULL;

CREATE TABLE clinical.qc_result (
    id                  uuid PRIMARY KEY,
    inbox_message_id    uuid NOT NULL REFERENCES messaging.inbox_message(id),
    instrument_id       text NOT NULL,
    control_name        text NULL,
    control_lot         text NULL,
    instrument_test_code text NOT NULL,
    raw_value           text NOT NULL,
    numeric_value       numeric NULL,
    units               text NULL,
    flags               text[] NOT NULL DEFAULT '{}',
    run_at              timestamptz NULL,
    received_at         timestamptz NOT NULL DEFAULT now()
);

-- Versioned reportable result. A correction is version n+1 in the same result_group.
CREATE TABLE clinical.result (
    id                  uuid PRIMARY KEY,
    result_group        uuid NOT NULL,
    version             integer NOT NULL,
    specimen_test_id    uuid NOT NULL REFERENCES clinical.specimen_test(id),
    instrument_result_id uuid NULL REFERENCES clinical.instrument_result(id),
    value_type          text NOT NULL CHECK (value_type IN ('numeric','qualitative','text')),
    value_text          text NOT NULL,
    numeric_value       numeric NULL,
    comparator          text NULL,
    units               text NULL,
    reference_range     text NULL,
    abnormal_flag       text NULL,
    instrument_flags    text[] NOT NULL DEFAULT '{}',
    flag_mismatch       boolean NOT NULL DEFAULT false,
    is_critical         boolean NOT NULL DEFAULT false,
    status              text NOT NULL DEFAULT 'received'
                        CHECK (status IN ('received','reviewed','approved','rejected','superseded')),
    supersedes_id       uuid NULL REFERENCES clinical.result(id),
    change_reason       text NULL,
    mapping_id          uuid NULL,
    created_at          timestamptz NOT NULL DEFAULT now(),
    UNIQUE (result_group, version)
);
CREATE INDEX ix_result_queue ON clinical.result (status, created_at) WHERE status IN ('received','reviewed');

CREATE TABLE clinical.review (
    id                  uuid PRIMARY KEY,
    result_id           uuid NOT NULL REFERENCES clinical.result(id),
    action              text NOT NULL CHECK (action IN ('review','approve','reject','comment')),
    actor               text NOT NULL,
    reason              text NULL,
    critical_notified_to text NULL,
    critical_method     text NULL,
    critical_notified_at timestamptz NULL,
    critical_read_back  boolean NULL,
    at                  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_review_result ON clinical.review (result_id);

-- AC4: an outbox row for a result can only exist if an approval review for that exact
-- result version exists. Enforced in the database, not only in application code.
CREATE FUNCTION clinical.require_approval() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.kind IN ('hmis.result','hmis.amendment') THEN
        IF NOT EXISTS (SELECT 1 FROM clinical.review r
                       WHERE r.id = NEW.approval_id AND r.result_id = NEW.aggregate_id AND r.action = 'approve') THEN
            RAISE EXCEPTION 'outbox row % for result % has no approval', NEW.id, NEW.aggregate_id;
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_outbox_requires_approval BEFORE INSERT OR UPDATE OF aggregate_id, approval_id ON messaging.outbox_message
    FOR EACH ROW EXECUTE FUNCTION clinical.require_approval();

-- ---------------------------------------------------------------- hmis
CREATE TABLE hmis.poll_cursor (
    name        text PRIMARY KEY,
    cursor      text NULL,
    updated_since timestamptz NULL,
    updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE hmis.delivery_attempt (
    id              bigserial PRIMARY KEY,
    outbox_id       uuid NOT NULL REFERENCES messaging.outbox_message(id),
    attempt_no      integer NOT NULL,
    operation       text NOT NULL,      -- submit | amend | read_back
    started_at      timestamptz NOT NULL,
    finished_at     timestamptz NULL,
    outcome         text NULL,          -- confirmed | rejected | retryable | unknown | not_found
    http_status     integer NULL,
    detail          text NULL
);
CREATE INDEX ix_delivery_attempt_outbox ON hmis.delivery_attempt (outbox_id);
