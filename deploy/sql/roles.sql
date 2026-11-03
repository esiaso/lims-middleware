-- Database roles from design 9.3. Run as the PostgreSQL superuser (held by hospital IT, not stored on the server).
-- Safe to run again: existing roles get the new passwords and an existing database is kept.
-- configure.ps1 generates the passwords and stores them with DPAPI.
-- Usage: psql -U postgres -d postgres -v ON_ERROR_STOP=1 -v owner_pw=... -v app_pw=... -v ro_pw=... -f roles.sql

SELECT format('CREATE ROLE %I LOGIN', r)
FROM unnest(ARRAY['lims_owner', 'lims_app', 'lims_readonly']) AS r
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) \gexec

ALTER ROLE lims_owner LOGIN PASSWORD :'owner_pw';
ALTER ROLE lims_app LOGIN PASSWORD :'app_pw';
ALTER ROLE lims_readonly LOGIN PASSWORD :'ro_pw';

SELECT 'CREATE DATABASE lims OWNER lims_owner'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'lims') \gexec

\connect lims
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO lims_owner;
-- After the first migration run (as lims_owner), apply grants.sql.
