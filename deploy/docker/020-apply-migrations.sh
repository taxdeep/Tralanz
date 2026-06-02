#!/usr/bin/env bash
set -euo pipefail

for migration in /docker-migrations/*.sql; do
  [ -e "$migration" ] || continue
  echo "Applying $migration"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -f "$migration"
done
