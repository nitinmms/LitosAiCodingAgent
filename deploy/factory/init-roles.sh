#!/bin/sh
# Runs once, when the database volume is first created. Creates the two roles the factory
# uses after bootstrap (ReadMe_LitosSoftwareFactory_V1.md §20):
#
#   factory_migrator  owns the schema; used only by `Litos.SoftwareFactory.Host --migrate`.
#   factory_runtime   least privilege; used by the host at runtime. It can read and write rows
#                     but cannot create, alter or drop anything.
#
# factory_admin (POSTGRES_USER) is for bootstrap only.
set -eu

psql -v ON_ERROR_STOP=1 -v migrator_password="$FACTORY_DB_MIGRATOR_PASSWORD" -v runtime_password="$FACTORY_DB_RUNTIME_PASSWORD" --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE factory_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE factory_runtime  LOGIN PASSWORD :'runtime_password';

GRANT CONNECT ON DATABASE litos_factory TO factory_migrator, factory_runtime;
GRANT USAGE, CREATE ON SCHEMA public TO factory_migrator;
GRANT USAGE ON SCHEMA public TO factory_runtime;

-- Everything the migrator creates from now on is usable, but not alterable, by the runtime role.
ALTER DEFAULT PRIVILEGES FOR ROLE factory_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO factory_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE factory_migrator IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO factory_runtime;
SQL
