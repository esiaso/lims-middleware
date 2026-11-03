-- Test panels: one HMIS order line (for example "CBC") stands for several catalog tests, each resulted on its own.
-- A catalog entry with panel_components is a panel; it is versioned and approved like any other catalog change.
ALTER TABLE clinical.test_catalog
    ADD COLUMN panel_components text[] NULL CHECK (panel_components IS NULL OR cardinality(panel_components) > 0);

-- A panel line becomes one order_test per component, all carrying the HMIS line id; results go back per component.
ALTER TABLE clinical.order_test ADD COLUMN panel_code text NULL;
ALTER TABLE clinical.order_test DROP CONSTRAINT order_test_order_id_hmis_line_id_key;
ALTER TABLE clinical.order_test ADD CONSTRAINT order_test_line_component_key UNIQUE (order_id, hmis_line_id, catalog_code);
