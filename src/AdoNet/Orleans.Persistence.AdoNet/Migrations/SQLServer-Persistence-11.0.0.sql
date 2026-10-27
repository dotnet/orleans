-- Run this migration for upgrading the SQL Server persistence queries for deployments created before 11.0.0.
-- The next grain state version wraps from 2147483647 back to 1 instead of overflowing the INT Version column.
-- Stop all silos using this database before applying the migration, then restart them to reload the queries.
-- The REPLACE is idempotent because the replacement does not contain the replaced text.

UPDATE OrleansQuery
SET QueryText = REPLACE(QueryText, 'Version + 1', 'Version % 2147483647 + 1')
WHERE QueryKey IN ('WriteToStorageKey', 'ClearStorageKey', 'DeleteStorageKey');
