-- 0002_operations: reconciliation work list (design 7.7), expected turnaround per test, catalog sync source.

ALTER TABLE clinical.test_catalog ADD COLUMN IF NOT EXISTS tat_minutes integer NOT NULL DEFAULT 240;
ALTER TABLE clinical.test_catalog ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT 'manual';
ALTER TABLE clinical.test_catalog ADD COLUMN IF NOT EXISTS proposed_by text NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_one_proposed ON clinical.test_catalog (code) WHERE status = 'proposed';

CREATE TABLE IF NOT EXISTS platform.reconciliation_item (
    id              uuid PRIMARY KEY,
    kind            text NOT NULL,          -- delivery_overdue | order_not_collected | pending_at_instrument | cancel_after_release | downtime_unlinked | critical_unacknowledged
    entity_type     text NOT NULL,
    entity_id       text NOT NULL,
    detail          text NOT NULL,
    severity        text NOT NULL DEFAULT 'warning' CHECK (severity IN ('info','warning','critical')),
    first_seen_at   timestamptz NOT NULL DEFAULT now(),
    last_seen_at    timestamptz NOT NULL DEFAULT now(),
    resolved_at     timestamptz NULL,
    resolved_by     text NULL,
    resolution      text NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_recon_open ON platform.reconciliation_item (kind, entity_id) WHERE resolved_at IS NULL;

CREATE TABLE IF NOT EXISTS platform.system_state (
    key         text PRIMARY KEY,
    value       text NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now()
);
