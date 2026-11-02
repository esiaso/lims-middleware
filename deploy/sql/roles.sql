-- Database roles from design 9.3. Run once as the PostgreSQL superuser (held by hospital IT, not on the server).
-- Replace the passwords: configure.ps1 generates them and stores them with DPAPI.
-- Usage: psql -U postgres -v owner_pw="'...'" -v app_pw="'...'" -v ro_pw="'...'" -f roles.sql

CREATE ROLE lims_owner LOGIN PASSWORD :owner_pw;
CREATE ROLE lims_app LOGIN PASSWORD :app_pw;
CREATE ROLE lims_readonly LOGIN PASSWORD :ro_pw;
CREATE DATABASE lims OWNER lims_owner;
\connect lims
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO lims_owner;
-- After the first migration run (as lims_owner), apply grants.sql.
