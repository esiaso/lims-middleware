-- Run as lims_owner after migrations. The application role gets DML only, and INSERT-only on audit.
GRANT USAGE ON SCHEMA platform, audit, messaging, instruments, clinical, hmis TO lims_app, lims_readonly;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA platform, messaging, instruments, clinical, hmis TO lims_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA platform, messaging, instruments, clinical, hmis, audit TO lims_app;
GRANT SELECT, INSERT ON audit.audit_event, audit.chain_anchor TO lims_app;
GRANT SELECT, INSERT, UPDATE ON audit.export_state TO lims_app;
REVOKE UPDATE, DELETE, TRUNCATE ON audit.audit_event FROM lims_app;
GRANT SELECT ON public.lims_schema_migration TO lims_app;
GRANT SELECT ON ALL TABLES IN SCHEMA platform, audit, messaging, instruments, clinical, hmis TO lims_readonly;
