/*
 This view queries broken foreign key and table constraints from databases using LIKE N'AdoNetSample%'.
 Usually broken constraints appear when the constraints are temporarily disabled during
 a performance intensive operation such as bulk-loading data in the database. If the
 constraints aren't fixed, the database don't use them and it likely incurs a significant
 performance hit.
*/
CREATE VIEW [Core].[SystemBrokenConstraintsView] AS
SELECT '[' + s.name + '].[' + o.name + ']' AS TableName, '[' + i.name + ']' AS BrokenConstraintName
FROM sys.foreign_keys i
    INNER JOIN sys.objects o ON i.parent_object_id = o.object_id
    INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE i.is_not_trusted = 1 AND i.is_not_for_replication = 0 AND DB_NAME() LIKE N'OneBoxDeployment.Core%'
UNION
SELECT '[' + s.name + '].[' + o.name + ']' AS TableName, '[' + i.name + ']' AS BrokenConstraintName
FROM sys.check_constraints i
    INNER JOIN sys.objects o ON i.parent_object_id = o.object_id
    INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE i.is_not_trusted = 1 AND i.is_not_for_replication = 0 AND DB_NAME() LIKE N'OneBoxDeployment.Core%';
