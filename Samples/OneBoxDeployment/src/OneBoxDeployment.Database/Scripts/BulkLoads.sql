-- Bulk loading is easy to break. See Git line feed settings and adjust appropriately. For more info:
-- https://www.mssqltips.com/sqlservertip/4648/sql-server-bulk-insert-row-terminator-issues/.
--
-- From SQL Server 2019 onwards, only CODEPAG=RAW is supported. See issues at
-- https://docs.microsoft.com/en-us/sql/t-sql/statements/bulk-insert-transact-sql?view=sql-server-2017.
PRINT N'Inserting [Core].[BulkData]...';
BULK INSERT [Core].[BulkData] FROM '$(ProjectDir)\TestData\BulkData.tsv' WITH(CODEPAGE=65001, FIELDTERMINATOR='\t', /*ROWTERMINATOR='0x0a',*/ TABLOCK, DATAFILETYPE='widechar', BATCHSIZE=10);
PRINT N'Inserting [Core].[BulkData] done.';