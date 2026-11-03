-- Delta checks: compare a numeric result with the same patient's previous released result.
-- A limit is either a percent change or an absolute change; both NULL means no delta check for the test.
ALTER TABLE clinical.test_catalog
    ADD COLUMN delta_percent     numeric NULL CHECK (delta_percent > 0),
    ADD COLUMN delta_absolute    numeric NULL CHECK (delta_absolute > 0),
    ADD COLUMN delta_window_days integer NOT NULL DEFAULT 30 CHECK (delta_window_days BETWEEN 1 AND 3650);

ALTER TABLE clinical.result
    ADD COLUMN delta_flag           boolean NOT NULL DEFAULT false,
    ADD COLUMN delta_previous_id    uuid NULL REFERENCES clinical.result(id),
    ADD COLUMN delta_previous_value numeric NULL;
