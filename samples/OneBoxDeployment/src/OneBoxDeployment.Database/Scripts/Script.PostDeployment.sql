/*
Post-Deployment Script Template
--------------------------------------------------------------------------------------
 This file contains SQL statements that will be appended to the build script.
 Use SQLCMD syntax to include a file in the post-deployment script.
 Example:      :r .\myfile.sql
 Use SQLCMD syntax to reference a variable in the post-deployment script.
 Example:      :setvar TableName MyTable
               SELECT * FROM [$(TableName)]
--------------------------------------------------------------------------------------
*/

SET XACT_ABORT, NOCOUNT ON;

:setvar __IsSqlCmdEnabled "True"

IF N'$(__IsSqlCmdEnabled)' NOT LIKE N'True'
BEGIN
	PRINT N'SQLCMD mode must be enabled to successfully execute this script.';
	SET NOEXEC ON;
END

IF N'$(Configuration)' = N'Debug' OR N'$(Configuration)' = N'Release'
BEGIN
	-- An assumption is made here that 'Debug' and 'Release' profiles are used only for testing in
	-- cases where recovery features or realistic features are not needed. This is done early
	-- in order to make other operations quicker.
	PRINT N'*** Setting recovery mode to ''SIMPLE''...***';
    ALTER DATABASE [$(DatabaseName)] SET RECOVERY SIMPLE;
	PRINT N'*** Setting recovery model to ''SIMPLE'' done. ***';
END

BEGIN TRANSACTION;
IF N'$(Configuration)' = N'Debug' OR N'$(Configuration)' = N'Release'
BEGIN
	PRINT N'*** Loading test data...***';
	:r .\Core.TestData.sql
	PRINT N'*** Done loading test data.***';
END

PRINT N'*** Loading Core.Constants constant data...***';
:r .\Core.Constants.sql
PRINT N'*** Done loading Core.Constants constant data.***';

PRINT N'*** Disabling constraint checking while bulkloading data... ***';
-- EXEC sp_MSforeachtable @command1="ALTER TABLE ? NOCHECK CONSTRAINT ALL";
PRINT N'*** Disabling constraint checking while bulkloading data done. ***';

PRINT '*** Loading bulk data... ***';
:r .\BulkLoads.sql
PRINT '*** Done loading bulk data. ***';

PRINT N'*** Fixing broken constraints... ***';
EXEC [Core].[SystemFixBrokenConstraints]
PRINT N'*** Done fixing broken constraints. ***';

IF EXISTS (SELECT 1 FROM sys.dm_db_persisted_sku_features WHERE feature_id = 100)
BEGIN
	PRINT N'*** Enabling [Core].[SettingNode] compression... ***';
	-- ALTER TABLE [Core].[SettingNode] REBUILD PARTITION = ALL WITH(DATA_COMPRESSION = PAGE);
	PRINT N'*** [Core].[SettingNode] compression enabled. ***';
END

PRINT N'*** Creating Orleans tables... ***';
:r .\CreateOrleansTables_SQLServer.sql
PRINT N'*** Done creating Orleans tables. ***';

COMMIT TRANSACTION;
