#!/bin/bash
set -e

SQLCMD=/opt/mssql-tools18/bin/sqlcmd

echo "Waiting a moment for SQL Server to be fully ready..."
sleep 2

for f in /docker-entrypoint-initdb.d/*.sql; do
    echo "Applying $f"
    "$SQLCMD" -S "$DB_HOST" -U sa -P "$SA_PASSWORD" -C -i "$f"
done

echo "All init scripts applied successfully."
