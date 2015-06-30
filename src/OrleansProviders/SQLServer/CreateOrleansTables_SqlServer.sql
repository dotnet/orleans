/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

/*
Implementation notes:

1) The general idea is that data is read and written through Orleans specific queries.
   Orleans operates on column names and types when reading and on parameter names and types when writing.
   
2) The implementations *must* preserve output names and types. Orleans reads query results by name and type.
   Vendor and deployment specific tuning is allowed and contributions are encouraged as long as the interface contract
   is maintained.

3) The implementations *must* preserve input query parameter names and types. Orleans uses these parameter names and types
   in executing the input queries. Vendor and deployment specific tuning is allowed and contributions are encouraged as
   long as the interface contract is maintained.
     
4) The implementation across vendor specific scripts *should* preserve the constraint names. This simplifies troubleshooting
   by virtue of uniform naming across concrete implementations.

5) ETag or VersionETag for Orleans is an opaque BINARY(n) or VARBINARY(n) column that Orleans transforms to a string and back to BINARY(n) or
   VARBINARY(n) when querying. The type of its actual implementation is not important.

6) For the sake of being explicit and removing ambiquity, Orleans expects some queries to return either TRUE or FALSE as an
   indication of success. Orleans reads this value as ADO.NET Boolean value.
   That is, affected rows or such does not matter. If an error is raised or an exception is thrown
   the query *must* ensure the entire transaction is rolled back and may either return FALSE or propagate the exception.
   Orleans handles exception as a failure and will (likely) retry.

   Additional note: along with the boolean success value other information could be provided too, such as an ETag
   of the operated entity and/or error codes equivalent to HTTP error codes.

   The operations *must* succeed atomically as mandated by Orleans membership protocol. For more
   information, see at
		http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html
		https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs
*/


-- Information of this view can be used to tune queries in database and deployment specific ways if needed.
CREATE VIEW [OrleansDatabaseInfo] AS
-- Version information derived from https://support.microsoft.com/en-us/kb/321185.
SELECT
    N'ProductName' AS [Id],
    CASE 
        WHEN LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), CHARINDEX('.', CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR)) - 1) = 8 THEN 'SQL Server 2000'
        WHEN LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), CHARINDEX('.', CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR)) - 1) = 9 THEN 'SQL Server 2005'
        WHEN LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), CHARINDEX('.', CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR)) - 1) = 10 
            AND RIGHT(LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), 5), 2) = 00 THEN 'SQL Server 2008'
        WHEN LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), CHARINDEX('.', CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR)) - 1) = 10 
            AND RIGHT(LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), 5), 2) = 50 THEN 'SQL Server 2008 R2' 
        WHEN LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), CHARINDEX('.', CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR)) - 1) = 11 THEN 'SQL Server 2012'
        WHEN LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), CHARINDEX('.', CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR)) - 1) = 12 THEN 'SQL Server 2014' 				
    END AS [Value],
    N'The database product name.' AS [Description]
UNION ALL
SELECT
    N'Database version' AS [Id], 
    CAST(SERVERPROPERTY('productversion') AS NVARCHAR) AS [Value],
    N'The version number of the database' AS [Description];

GO

-- These settings improves throughput of the database by reducing locking by better separating readers from writers.
-- The assumption here is that no one tries to run Orleans on older than SQL Server 2000. These capabilities are supported
-- on SQL Server 2005 and newer.
IF(NOT EXISTS(SELECT [Value] FROM [OrleansDatabaseInfo] WHERE Id = N'ProductName' AND [Value] IN (N'SQL Server 2000')))
BEGIN
    -- SQL Server 2012 and newer can refer to itself as CURRENT. Older ones need a workaround.
    DECLARE @current NVARCHAR(256);
    DECLARE @snapshotSettings NVARCHAR(612);
    
    SELECT @current = (SELECT DB_NAME());
    SET @snapshotSettings = N'ALTER DATABASE ' + @current + N' SET READ_COMMITTED_SNAPSHOT ON; ALTER DATABASE ' + @current + N' SET ALLOW_SNAPSHOT_ISOLATION ON;';
    	
	EXECUTE sp_executesql @snapshotSettings;	
END;

GO

-- This table defines Orleans operational queries. Orleans uses these to manage its operations,
-- these are the only queries Orleans issues to the database.
-- These can be redefined provided the stated interface principles hold.
CREATE TABLE [OrleansQuery]
(	
    [Key] VARCHAR(64) NOT NULL,
    [Query] NVARCHAR(MAX) NOT NULL,
    [Description] NVARCHAR(MAX) NOT NULL

	CONSTRAINT OrleansQuery_Key PRIMARY KEY([Key])
);


-- There will ever be only one (active) membership version table version column of which will be updated periodically.
-- See table description at http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html. The following
-- IF-ELSE does SQL Server version detection and defines separate table structures and queries for them.
-- Orleans issues the queries as defined in [OrleansQuery] and operates through parameter names and types with no
-- regard to other matters.
IF(NOT EXISTS(SELECT [Value] FROM [OrleansDatabaseInfo] WHERE Id = N'ProductName' AND [Value] IN (N'SQL Server 2000')))
BEGIN
	-- These table definitions are SQL Server 2005 and later. The differences are
	-- the ETag is ROWVersion in SQL Server 2005 and later whereas in SQL Server 2000 UNIQUEIDENTIFIER is used
	-- and SQL Server 2005 and later use DATETIME2(7) and associated functions whereas SQL Server uses DATETIME.
	CREATE TABLE [OrleansMembershipVersionTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[Timestamp] DATETIME2(7) NOT NULL, 
		[Version] BIGINT NOT NULL,		
		[ETag] ROWVERSION NOT NULL,
    
		CONSTRAINT PK_OrleansMembershipVersionTable_DeploymentId PRIMARY KEY ([DeploymentId])	
	);

	CREATE TABLE [OrleansMembershipTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[Address] VARCHAR(45) NOT NULL, 
		[Port] INT NOT NULL, 
		[Generation] INT NOT NULL, 
		[HostName] NVARCHAR(150) NOT NULL, 
		[Status] INT NOT NULL, 
		[ProxyPort] INT NULL, 
		[Primary] BIT NULL, 
		[RoleName] NVARCHAR(150) NULL, 
		[InstanceName] NVARCHAR(150) NULL, 
		[UpdateZone] INT NULL, 
		[FaultZone] INT NULL,		
		[SuspectingSilos] NVARCHAR(MAX) NULL, 
		[SuspectingTimes] NVARCHAR(MAX) NULL, 
		[StartTime] DATETIME2(7) NOT NULL, 
		[IAmAliveTime] DATETIME2(7) NOT NULL,			
		[ETag] ROWVERSION NOT NULL,
    
		-- A refactoring note: This combination needs to be unique, currently enforced by making it a primary key.
		-- See more information at http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html.
		CONSTRAINT PK_OrleansMembershipTable_DeploymentId PRIMARY KEY([DeploymentId], [Address], [Port], [Generation]),	
		CONSTRAINT FK_OrleansMembershipTable_OrleansMembershipVersionTable_DeploymentId FOREIGN KEY([DeploymentId]) REFERENCES [OrleansMembershipVersionTable]([DeploymentId])
	);

	CREATE TABLE [OrleansRemindersTable]
	(
		[ServiceId] NVARCHAR(150) NOT NULL, 
		[GrainId] NVARCHAR(150) NOT NULL, 
		[ReminderName] NVARCHAR(150) NOT NULL,
		[StartTime] DATETIME2(7) NOT NULL, 
		[Period] INT NOT NULL,
		[GrainIdConsistentHash] INT NOT NULL,
		[ETag] ROWVERSION NOT NULL,
    
		CONSTRAINT PK_OrleansRemindersTable_ServiceId_GrainId_ReminderName PRIMARY KEY([ServiceId], [GrainId], [ReminderName])
	);

	CREATE TABLE [OrleansStatisticsTable]
	(
		[OrleansStatisticsTableId] INT IDENTITY(1,1) NOT NULL,
		[DeploymentId] NVARCHAR(150) NOT NULL,      
		[Timestamp] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), 
		[Id] NVARCHAR(250) NOT NULL,     
		[HostName] NVARCHAR(150) NOT NULL, 
		[Name] NVARCHAR(150) NULL, 
		[IsDelta] BIT NOT NULL, 
		[StatValue] NVARCHAR(250) NOT NULL,
		[Statistic] NVARCHAR(250) NOT NULL,

		CONSTRAINT OrleansStatisticsTable_OrleansStatisticsTableId PRIMARY KEY([OrleansStatisticsTableId])	
	);
	
	CREATE TABLE [OrleansClientMetricsTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[ClientId] NVARCHAR(150) NOT NULL, 
		[Timestamp] DATETIME2(7) NOT NULL, 
		[Address] VARCHAR(45) NOT NULL, 
		[HostName] NVARCHAR(150) NOT NULL, 
		[CPU] FLOAT NOT NULL,
		[Memory] BIGINT NOT NULL,
		[SendQueue] INT NOT NULL, 
		[ReceiveQueue] INT NOT NULL, 
		[SentMessages] BIGINT NOT NULL,
		[ReceivedMessages] BIGINT NOT NULL,
		[ConnectedGatewayCount] BIGINT NOT NULL,
    
		CONSTRAINT PK_OrleansClientMetricsTable_DeploymentId_ClientId PRIMARY KEY([DeploymentId], [ClientId])
	);

	CREATE TABLE [OrleansSiloMetricsTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[SiloId] NVARCHAR(150) NOT NULL, 
		[Timestamp] DATETIME2(7) NOT NULL, 
		[Address] VARCHAR(45) NOT NULL, 
		[Port] INT NOT NULL, 
		[Generation] INT NOT NULL, 
		[HostName] NVARCHAR(150) NOT NULL, 
		[GatewayAddress] VARCHAR(45) NULL, 
		[GatewayPort] INT NULL, 
		[CPU] FLOAT NOT NULL,
		[Memory] BIGINT NOT NULL,
		[Activations] INT NOT NULL,
		[RecentlyUsedActivations] INT NOT NULL,
		[SendQueue] INT NOT NULL, 
		[ReceiveQueue] INT NOT NULL, 
		[RequestQueue] BIGINT NOT NULL,
		[SentMessages] BIGINT NOT NULL,
		[ReceivedMessages] BIGINT NOT NULL,
		[LoadShedding] BIT NOT NULL,
		[ClientCount] BIGINT NOT NULL,
    
		CONSTRAINT PK_OrleansSiloMetricsTable_DeploymentId_SiloId PRIMARY KEY([DeploymentId], [SiloId]),
		CONSTRAINT FK_OrleansSiloMetricsTable_OrleansMembershipVersionTable_DeploymentId FOREIGN KEY([DeploymentId]) REFERENCES [OrleansMembershipVersionTable]([DeploymentId])
	);

	-- Some of the Orleans queries are version specific due to ROWVERSION in SQL Server 2005 and later.
	-- ROWVERSION is applied automatically whereas an etag mechanism of using UNIQUEIDENTIFIER in SQL Server is not.
	-- Also some queries could be tuned better on SQL Server 2005 and later such as error handling or SQL Server 2008
	-- and later using MERGE for UPSERT (reminders).
	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'UpdateIAmAlivetimeKey',
		N'
		-- This is not expected to never fail by Orleans, so return value
		-- is not needed nor is it checked.
		SET NOCOUNT ON;
		BEGIN TRANSACTION;
		UPDATE [OrleansMembershipTable]
		SET
			IAmAliveTime = @iAmAliveTime	    
		WHERE
			([DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL)
			AND ([Address] = @address AND @address IS NOT NULL)
			AND ([Port] = @port AND @port IS NOT NULL)
			AND ([Generation] = @generation AND @generation IS NOT NULL);
		COMMIT TRANSACTION;',
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		-- There should ever be only one version row. A new one is tried to insert only once when a silo starts.
		-- The concurrency is virtually non-existent, but for the sake robustness, appropriate locks are taken.
		'InsertMembershipVersionKey',
		N'SET NOCOUNT ON;
		BEGIN TRANSACTION;		
		INSERT INTO [OrleansMembershipVersionTable]
		(
			[DeploymentId],
			[Timestamp],
			[Version]	    
		)
		SELECT	
			@deploymentId,
			SYSUTCDATETIME(),
			@version        
		WHERE NOT EXISTS
		(			
			SELECT 1
			FROM [OrleansMembershipVersionTable] WITH(HOLDLOCK, XLOCK, ROWLOCK)
			WHERE [DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL
		);
                                        
		IF @@ROWCOUNT > 0
		BEGIN
			COMMIT TRANSACTION;
			SELECT CAST(1 AS BIT);
		END
		ELSE
		BEGIN
			ROLLBACK TRANSACTION;
			SELECT CAST(0 AS BIT);
		END',
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		-- There should ever be only one version row. A new one is tried to insert only once when a silo starts.
		-- The concurrency is virtually non-existent, but for the sake robustness, appropriate locks are taken.
		'InsertMembershipKey',
		N'SET NOCOUNT ON;
		BEGIN TRANSACTION; --  @@TRANCOUNT = 0 -> +1.
		-- There is no need to check the condition for inserting
		-- as the necessary condition with regard to table membership
		-- protocol is enforced as part of the primary key definition.
		-- Inserting will fail if there is already a membership
		-- row with the same
		-- * [DeploymentId] = @deploymentId
		-- * [Address]		= @address
		-- * [Port]			= @port
		-- * [Generation]	= @generation
		--
		-- For more information on table membership protocol insert see at
		-- http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html and at
		-- https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs
		INSERT INTO [OrleansMembershipTable]
		(
			[DeploymentId],
			[Address],
			[Port],
			[Generation],
			[HostName],
			[Status],
			[ProxyPort],
			[Primary],
			[RoleName],
			[InstanceName],
			[UpdateZone],
			[FaultZone],
			[SuspectingSilos],
			[SuspectingTimes],
			[StartTime],
			[IAmAliveTime]
		)
		VALUES
		(
			@deploymentId,
			@address,
			@port,
			@generation,
			@hostName,
			@status,
			@proxyPort,
			@primary,
			@roleName,
			@instanceName,
			@updateZone,
			@faultZone,
			@suspectingSilos,
			@suspectingTimes,
			@startTime,
			@iAmAliveTime
		);

		IF @@ROWCOUNT = 0 ROLLBACK TRANSACTION;

		IF @@TRANCOUNT > 0
		BEGIN
			-- The transaction has not been rolled back. The following
			-- update must succeed or else the whole transaction needs
			-- to be rolled back.
			UPDATE [OrleansMembershipVersionTable]
			SET
				[Timestamp]	= SYSUTCDATETIME(),
				[Version]	= @version
			WHERE
				([DeploymentId]	= @deploymentId AND @deploymentId IS NOT NULL)
				AND ([ETag]		= @versionEtag AND @versionEtag IS NOT NULL);

			-- Here the rowcount should always be either zero (no update)
			-- or one (exactly one entry updated). Having more means that multiple
			-- lines matched the condition. This should not be possible, but checking
			-- only for zero allows the system to function and there is no harm done
			-- besides potentially superfluous updates.
			IF @@ROWCOUNT = 0 ROLLBACK TRANSACTION;
		END

		IF @@TRANCOUNT > 0
		BEGIN
			COMMIT TRANSACTION;
			SELECT CAST(1 AS BIT);
		END
		ELSE
		BEGIN	
			SELECT CAST(0 AS BIT);
		END', 
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'UpdateMembershipKey',
		N'SET NOCOUNT ON;
		BEGIN TRANSACTION; --  @@TRANCOUNT + 1

		-- For more information on table membership protocol update see at
		-- http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html and at
		-- https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs.
		UPDATE [OrleansMembershipTable]
		SET
			[Address]			= @address,
			[Port]				= @port,
			[Generation]		= @generation,
			[HostName]			= @hostName,
			[Status]			= @status,
			[ProxyPort]			= @proxyPort,
			[Primary]			= @primary,
			[RoleName]			= @roleName,
			[InstanceName]		= @instanceName,
			[UpdateZone]		= @updateZone,
			[FaultZone]			= @faultZone,
			[SuspectingSilos]	= @suspectingSilos,
			[SuspectingTimes]	= @suspectingTimes,
			[StartTime]			= @startTime,
			[IAmAliveTime]		= @iAmAliveTime
		WHERE
			([DeploymentId]		= @deploymentId AND @deploymentId IS NOT NULL)
			AND ([Address]		= @address AND @address IS NOT NULL)
			AND ([Port]			= @port AND @port IS NOT NULL)
			AND ([Generation]	= @generation AND @generation IS NOT NULL)
			AND ([ETag]			= @etag and @etag IS NOT NULL)

		IF @@ROWCOUNT = 0 ROLLBACK TRANSACTION;

		IF @@TRANCOUNT > 0
		BEGIN
			-- The transaction has not been rolled back. The following
			-- update must succeed or else the whole transaction needs
			-- to be rolled back.
			UPDATE [OrleansMembershipVersionTable]
			SET
				[Timestamp]	= SYSUTCDATETIME(),
				[Version]	= @version
			WHERE
				DeploymentId	= @deploymentId AND @deploymentId IS NOT NULL
				AND ETag		= @versionEtag AND @versionEtag IS NOT NULL;

			IF @@ROWCOUNT = 0 ROLLBACK TRANSACTION;
		END

		IF @@TRANCOUNT > 0
		BEGIN
			COMMIT TRANSACTION;
			SELECT CAST(1 AS BIT);
		END
		ELSE
		BEGIN	
			SELECT CAST(0 AS BIT);
		END',
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'UpsertReminderRowKey',
		N'SET XACT_ABORT, NOCOUNT ON;    
		BEGIN TRANSACTION;                    
		IF NOT EXISTS(SELECT 1 FROM [OrleansRemindersTable]  WITH(UPDLOCK, HOLDLOCK) WHERE
			[ServiceId] = @serviceId AND @serviceId IS NOT NULL
			AND [GrainId] = @grainId AND @grainId IS NOT NULL
			AND [ReminderName] = @reminderName AND @reminderName IS NOT NULL)
		BEGIN        
			INSERT INTO [OrleansRemindersTable]
			(
				[ServiceId],
				[GrainId],
				[ReminderName],
				[StartTime],
				[Period],
				[GrainIdConsistentHash]
			)
			OUTPUT inserted.ETag
			VALUES
			(
				@serviceId,
				@grainId,
				@reminderName,
				@startTime,
				@period,
				@grainIdConsistentHash
			);
		END
		ELSE
		BEGIN        
			UPDATE [OrleansRemindersTable]	
			SET
				[StartTime]             = @startTime,
				[Period]                = @period,
				[GrainIdConsistentHash] = @grainIdConsistentHash
			OUTPUT inserted.ETag
			WHERE
				[ServiceId] = @serviceId AND @serviceId IS NOT NULL
				AND [GrainId] = @grainId AND @grainId IS NOT NULL
				AND [ReminderName] = @reminderName AND @reminderName IS NOT NULL
				AND ETag = @etag AND @etag IS NOT NULL;
			END	
			COMMIT TRANSACTION;',
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'UpsertReportClientMetricsKey',
		N'SET XACT_ABORT, NOCOUNT ON;		
		BEGIN TRANSACTION;     
		IF EXISTS(SELECT 1 FROM [OrleansClientMetricsTable]  WITH(UPDLOCK, HOLDLOCK) WHERE
			[DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL
			AND [ClientId] = @clientId AND @clientId IS NOT NULL)
		BEGIN
			UPDATE [OrleansClientMetricsTable]
			SET			
				[Timestamp] = SYSUTCDATETIME(),
				[Address] = @address,
				[HostName] = @hostName,
				[CPU] = @cpuUsage,
				[Memory] = @memoryUsage,
				[SendQueue] = @sendQueueLength,
				[ReceiveQueue] = @receiveQueueLength,
				[SentMessages] = @sentMessagesCount,
				[ReceivedMessages] = @receivedMessagesCount,
				[ConnectedGatewayCount] = @connectedGatewaysCount
		END
		ELSE
		BEGIN	
			INSERT INTO [OrleansClientMetricsTable]
			(
				[DeploymentId],
				[ClientId],
				[Timestamp],
				[Address],			
				[HostName],
				[CPU],
				[Memory],
				[SendQueue],
				[ReceiveQueue],
				[SentMessages],
				[ReceivedMessages],
				[ConnectedGatewayCount]
			)
			VALUES
			(
				@deploymentId,
				@clientId,
				SYSUTCDATETIME(),
				@address,
				@hostName,
				@cpuUsage,
				@memoryUsage,
				@sendQueueLength,
				@receiveQueueLength,
				@sentMessagesCount,
				@receivedMessagesCount,
				@connectedGatewaysCount
			);
		END
		COMMIT TRANSACTION;',
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'UpsertSiloMetricsKey',
		N'SET XACT_ABORT, NOCOUNT ON;		
		BEGIN TRANSACTION;
		IF EXISTS(SELECT 1 FROM  [OrleansSiloMetricsTable]  WITH(updlock, holdlock) WHERE
			[DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL
			AND [SiloId] = @siloId AND @siloId IS NOT NULL)
		BEGIN
			UPDATE [OrleansSiloMetricsTable]
			SET
				Timestamp = SYSUTCDATETIME(),
				Address = @address,
				Port = @port,
				Generation = @generation,
				HostName = @hostName,
				GatewayAddress = @gatewayAddress,
				GatewayPort = @gatewayPort,
				CPU = @cpuUsage,
				Memory = @memoryUsage,
				Activations = @activationsCount,
				RecentlyUsedActivations = @recentlyUsedActivationsCount,
				SendQueue = @sendQueueLength,
				ReceiveQueue = @receiveQueueLength,
				RequestQueue = @requestQueueLength,
				SentMessages = @sentMessagesCount,
				ReceivedMessages = @receivedMessagesCount,
				LoadShedding = @isOverloaded,
				ClientCount = @clientCount;
		END
		ELSE
		BEGIN
			INSERT INTO [OrleansSiloMetricsTable]
			(
				[DeploymentId],
				[SiloId],
				[Timestamp],
				[Address],
				[Port],
				[Generation],
				[HostName],
				[GatewayAddress],
				[GatewayPort],
				[CPU],
				[Memory],
				[Activations],
				[RecentlyUsedActivations],
				[SendQueue],
				[ReceiveQueue],
				[RequestQueue],
				[SentMessages],	
				[ReceivedMessages],
				[LoadShedding],
				[ClientCount]
			)
			VALUES
			(
				@deploymentId,
				@siloId,
				SYSUTCDATETIME(),
				@address,
				@port,
				@generation,
				@hostName,
				@gatewayAddress,
				@gatewayPort,
				@cpuUsage,
				@memoryUsage,
				@activationsCount,
				@recentlyUsedActivationsCount,
				@sendQueueLength,
				@receiveQueueLength,
				@requestQueueLength,
				@sentMessagesCount,
				@receivedMessagesCount,
				@isOverloaded,
				@clientCount
			);
		END
		COMMIT TRANSACTION;',
		N''
	);
END
ELSE
BEGIN
	-- These table definitions are for SQL Server 2000.
	CREATE TABLE [OrleansMembershipVersionTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[Timestamp] DATETIME NOT NULL, 
		[Version] BIGINT NOT NULL,

		-- ETag should also always be unique, but there will ever be only one row.
		-- This is BINARY(16) to be as much compatible with the later SQL Server
		-- editions as possible.
		[ETag] BINARY(16) NOT NULL,
    
		CONSTRAINT PK_OrleansMembershipVersionTable_DeploymentId PRIMARY KEY([DeploymentId])	
	);

	CREATE TABLE [OrleansMembershipTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[Address] VARCHAR(45) NOT NULL, 
		[Port] INT NOT NULL, 
		[Generation] INT NOT NULL, 
		[HostName] NVARCHAR(150) NOT NULL, 
		[Status] INT NOT NULL, 
		[ProxyPort] INT NULL, 
		[Primary] BIT NULL, 
		[RoleName] NVARCHAR(150) NULL, 
		[InstanceName] NVARCHAR(150) NULL, 
		[UpdateZone] INT NULL, 
		[FaultZone] INT NULL,		
		[SuspectingSilos] NTEXT NULL, 
		[SuspectingTimes] NTEXT NULL, 
		[StartTime] DATETIME NOT NULL, 
		[IAmAliveTime] DATETIME NOT NULL,
	
		-- This ETag should always be unique for a given primary key.
		-- This is BINARY(16) to be as much compatible with the later SQL Server
		-- editions as possible. 
		[ETag] BINARY(16) NOT NULL,
    
		-- A refactoring note: This combination needs to be unique, currently enforced by making it a primary key.
		-- See more information at http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html.
		CONSTRAINT PK_OrleansMembershipTable_DeploymentId PRIMARY KEY([DeploymentId], [Address], [Port], [Generation]),	
		CONSTRAINT FK_OrleansMembershipTable_OrleansMembershipVersionTable_DeploymentId FOREIGN KEY([DeploymentId]) REFERENCES [OrleansMembershipVersionTable]([DeploymentId])
	);

	CREATE TABLE [OrleansRemindersTable]
	(
		[ServiceId] NVARCHAR(150) NOT NULL, 
		[GrainId] NVARCHAR(150) NOT NULL, 
		[ReminderName] NVARCHAR(150) NOT NULL,
		[StartTime] DATETIME NOT NULL, 
		[Period] INT NOT NULL,
		[GrainIdConsistentHash] INT NOT NULL,
				
		-- This is BINARY(16) to be as much compatible with the later SQL Server
		-- editions as possible.
		[ETag] BINARY(16) NOT NULL,
    
		CONSTRAINT PK_OrleansRemindersTable_ServiceId_GrainId_ReminderName PRIMARY KEY([ServiceId], [GrainId], [ReminderName])
	);
	
	CREATE TABLE [OrleansStatisticsTable]
	(
		[OrleansStatisticsTableId] INT IDENTITY(1,1) NOT NULL,
		[DeploymentId] NVARCHAR(150) NOT NULL,      
		[Timestamp] DATETIME NOT NULL DEFAULT GETUTCDATE(), 
		[Id] NVARCHAR(250) NOT NULL,     
		[HostName] NVARCHAR(150) NOT NULL, 
		[Name] NVARCHAR(150) NULL, 
		[IsDelta] BIT NOT NULL, 
		[StatValue] NVARCHAR(250) NOT NULL,
		[Statistic] NVARCHAR(250) NOT NULL,

		CONSTRAINT OrleansStatisticsTable_OrleansStatisticsTableId PRIMARY KEY([OrleansStatisticsTableId])	
	);

	CREATE TABLE [OrleansClientMetricsTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[ClientId] NVARCHAR(150) NOT NULL, 
		[Timestamp] DATETIME NOT NULL, 
		[Address] VARCHAR(45) NOT NULL, 
		[HostName] NVARCHAR(150) NOT NULL, 
		[CPU] FLOAT NOT NULL,
		[Memory] BIGINT NOT NULL,
		[SendQueue] INT NOT NULL, 
		[ReceiveQueue] INT NOT NULL, 
		[SentMessages] BIGINT NOT NULL,
		[ReceivedMessages] BIGINT NOT NULL,
		[ConnectedGatewayCount] BIGINT NOT NULL,
    
		CONSTRAINT PK_OrleansClientMetricsTable_DeploymentId_ClientId PRIMARY KEY([DeploymentId], [ClientId])
	);

	CREATE TABLE [OrleansSiloMetricsTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[SiloId] NVARCHAR(150) NOT NULL, 
		[Timestamp] DATETIME NOT NULL, 
		[Address] VARCHAR(45) NOT NULL, 
		[Port] INT NOT NULL, 
		[Generation] INT NOT NULL, 
		[HostName] NVARCHAR(150) NOT NULL, 
		[GatewayAddress] VARCHAR(45) NULL, 
		[GatewayPort] INT NULL, 
		[CPU] FLOAT NOT NULL,
		[Memory] BIGINT NOT NULL,
		[Activations] INT NOT NULL,
		[RecentlyUsedActivations] INT NOT NULL,
		[SendQueue] INT NOT NULL, 
		[ReceiveQueue] INT NOT NULL, 
		[RequestQueue] BIGINT NOT NULL,
		[SentMessages] BIGINT NOT NULL,
		[ReceivedMessages] BIGINT NOT NULL,
		[LoadShedding] BIT NOT NULL,
		[ClientCount] BIGINT NOT NULL,
    
		CONSTRAINT PK_OrleansSiloMetricsTable_DeploymentId_SiloId PRIMARY KEY([DeploymentId], [SiloId]),
		CONSTRAINT FK_OrleansSiloMetricsTable_OrleansMembershipVersionTable_DeploymentId FOREIGN KEY([DeploymentId]) REFERENCES [OrleansMembershipVersionTable]([DeploymentId])
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'UpdateIAmAlivetimeKey',
		N'
		-- This is not expected to never fail by Orleans, so return value
		-- is not needed nor is it checked.
		SET NOCOUNT ON;
		BEGIN TRANSACTION;
		UPDATE [OrleansMembershipTable]
		SET
			IAmAliveTime = @iAmAliveTime,
			ETag = NEWID()
		WHERE
			([DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL)
			AND ([Address] = @address AND @address IS NOT NULL)
			AND ([Port] = @port AND @port IS NOT NULL)
			AND ([Generation] = @generation AND @generation IS NOT NULL);
		COMMIT TRANSACTION;',
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'InsertMembershipVersionKey',
		N'SET NOCOUNT ON;
		BEGIN TRANSACTION;
		INSERT INTO [OrleansMembershipVersionTable]
		(
			[DeploymentId],
			[TimeStamp],
			[Version],
			[ETag]
		)
		SELECT	
			@deploymentId,
			GETUTCDATE(),
			@version,
			NEWID()
		WHERE NOT EXISTS
		(
			SELECT 1
			FROM OrleansMembershipVersionTable WITH(HOLDLOCK, XLOCK, ROWLOCK)
			WHERE [DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL
		);
                                        
		IF @@ROWCOUNT > 0
		BEGIN
			COMMIT TRANSACTION;
			SELECT CAST(1 AS BIT);
		END
		ELSE
		BEGIN
			ROLLBACK TRANSACTION;
			SELECT CAST(0 AS BIT);
		END',
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'InsertMembershipKey',
		N'SET NOCOUNT ON;
		BEGIN TRANSACTION; --  @@TRANCOUNT = 0 -> +1.
		-- There is no need to check the condition for inserting
		-- as the necessary condition with regard to table membership
		-- protocol is enforced as part of the primary key definition.
		-- Inserting will fail if there is already a membership
		-- row with the same
		-- * [DeploymentId] = @deploymentId
		-- * [Address]		= @address
		-- * [Port]			= @port
		-- * [Generation]	= @generation
		--
		-- For more information on table membership protocol insert see at
		-- http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html and at
		-- https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs
		INSERT INTO [OrleansMembershipTable]
		(
			[DeploymentId],
			[Address],
			[Port],
			[Generation],
			[HostName],
			[Status],
			[ProxyPort],
			[Primary],
			[RoleName],
			[InstanceName],
			[UpdateZone],
			[FaultZone],
			[SuspectingSilos],
			[SuspectingTimes],
			[StartTime],
			[IAmAliveTime],
			[ETag]
		)
		VALUES
		(
			@deploymentId,
			@address,
			@port,
			@generation,
			@hostName,
			@status,
			@proxyPort,
			@primary,
			@roleName,
			@instanceName,
			@updateZone,
			@faultZone,
			@suspectingSilos,
			@suspectingTimes,
			@startTime,
			@iAmAliveTime,
			NEWID()
		);

		IF @@ROWCOUNT = 0 ROLLBACK TRANSACTION;

		IF @@TRANCOUNT > 0
		BEGIN
			-- The transaction has not been rolled back. The following
			-- update must succeed or else the whole transaction needs
			-- to be rolled back.
			UPDATE [OrleansMembershipVersionTable]
			SET
				[Timestamp]	= GETUTCDATE(),
				[Version]	= @version,
				[ETag]		= NEWID()
			WHERE
				([DeploymentId]	= @deploymentId AND @deploymentId IS NOT NULL)
				AND ([ETag]		= @versionEtag AND @versionEtag IS NOT NULL);

			-- Here the rowcount should always be either zero (no update)
			-- or one (exactly one entry updated). Having more means that multiple
			-- lines matched the condition. This should not be possible, but checking
			-- only for zero allows the system to function and there is no harm done
			-- besides potentially superfluous updates.
			IF @@ROWCOUNT = 0 ROLLBACK TRANSACTION;
		END

		IF @@TRANCOUNT > 0
		BEGIN
			COMMIT TRANSACTION;
			SELECT CAST(1 AS BIT);
		END
		ELSE
		BEGIN	
			SELECT CAST(0 AS BIT);
		END', 
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'UpdateMembershipKey',
		N'SET NOCOUNT ON;
		BEGIN TRANSACTION; --  @@TRANCOUNT + 1

		-- For more information on table membership protocol update see at
		-- http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html and at
		-- https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs.
		UPDATE [OrleansMembershipTable]
		SET
			[Address]			= @address,
			[Port]				= @port,
			[Generation]		= @generation,
			[HostName]			= @hostName,
			[Status]			= @status,
			[ProxyPort]			= @proxyPort,
			[Primary]			= @primary,
			[RoleName]			= @roleName,
			[InstanceName]		= @instanceName,
			[UpdateZone]		= @updateZone,
			[FaultZone]			= @faultZone,
			[SuspectingSilos]	= @suspectingSilos,
			[SuspectingTimes]	= @suspectingTimes,
			[StartTime]			= @startTime,
			[IAmAliveTime]		= @iAmAliveTime,
			[ETag]				= NEWID()
		WHERE
			([DeploymentId]		= @deploymentId AND @deploymentId IS NOT NULL)
			AND ([Address]		= @address AND @address IS NOT NULL)
			AND ([Port]			= @port AND @port IS NOT NULL)
			AND ([Generation]	= @generation AND @generation IS NOT NULL)
			AND ([ETag]			= @etag and @etag IS NOT NULL)

		IF @@ROWCOUNT = 0 ROLLBACK TRANSACTION;

		IF @@TRANCOUNT > 0
		BEGIN
			-- The transaction has not been rolled back. The following
			-- update must succeed or else the whole transaction needs
			-- to be rolled back.
			UPDATE [OrleansMembershipVersionTable]
			SET
				[Timestamp]	= GETUTCDATE(),
				[Version]	= @version,
				[ETag]		= NEWID()
			WHERE
				DeploymentId	= @deploymentId AND @deploymentId IS NOT NULL
				AND ETag		= @versionEtag AND @versionEtag IS NOT NULL;

			IF @@ROWCOUNT = 0 ROLLBACK TRANSACTION;
		END

		IF @@TRANCOUNT > 0
		BEGIN
			COMMIT TRANSACTION;
			SELECT CAST(1 AS BIT);
		END
		ELSE
		BEGIN	
			SELECT CAST(0 AS BIT);
		END',
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'UpsertReminderRowKey',
		N'SET XACT_ABORT, NOCOUNT ON;
		DECLARE @newEtag AS BINARY(16) = NEWID();
		BEGIN TRANSACTION;                    
		IF NOT EXISTS(SELECT 1 FROM [OrleansRemindersTable]  WITH(updlock, holdlock) WHERE
			[ServiceId] = @serviceId AND @serviceId IS NOT NULL
			AND [GrainId] = @grainId AND @grainId IS NOT NULL
			AND [ReminderName] = @reminderName AND @reminderName IS NOT NULL)
		BEGIN        
			INSERT INTO [OrleansRemindersTable]
			(
				[ServiceId],
				[GrainId],
				[ReminderName],
				[StartTime],
				[Period],
				[GrainIdConsistentHash],
				[ETag]
			)
			VALUES
			(
				@serviceId,
				@grainId,
				@reminderName,
				@startTime,
				@period,
				@grainIdConsistentHash,
				@newEtag
			);
		END
		ELSE
		BEGIN        
			UPDATE [OrleansRemindersTable]
			SET				
				[StartTime]             = @startTime,
				[Period]                = @period,
				[GrainIdConsistentHash] = @grainIdConsistentHash,
				[ETag]                  = @newEtag
			WHERE
				[ServiceId] = @serviceId AND @serviceId IS NOT NULL
				AND [GrainId] = @grainId AND @grainId IS NOT NULL
				AND [ReminderName] = @reminderName AND @reminderName IS NOT NULL
				AND ETag = @etag AND @etag IS NOT NULL;
		END	
		COMMIT TRANSACTION;
		SELECT @newEtag;',
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'UpsertReportClientMetricsKey',
		N'SET XACT_ABORT, NOCOUNT ON;		
		BEGIN TRANSACTION;     
		IF EXISTS(SELECT 1 FROM [OrleansClientMetricsTable]  WITH(updlock, holdlock) WHERE
			[DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL
			AND [ClientId] = @clientId AND @clientId IS NOT NULL)
		BEGIN
			UPDATE [OrleansClientMetricsTable]
			SET			
				[Timestamp] = GETUTCDATE(),
				[Address] = @address,
				[HostName] = @hostName,
				[CPU] = @cpuUsage,
				[Memory] = @memoryUsage,
				[SendQueue] = @sendQueueLength,
				[ReceiveQueue] = @receiveQueueLength,
				[SentMessages] = @sentMessagesCount,
				[ReceivedMessages] = @receivedMessagesCount,
				[ConnectedGatewayCount] = @connectedGatewaysCount
		END
		ELSE
		BEGIN	
			INSERT INTO [OrleansClientMetricsTable]
			(
				[DeploymentId],
				[ClientId],
				[Timestamp],
				[Address],			
				[HostName],
				[CPU],
				[Memory],
				[SendQueue],
				[ReceiveQueue],
				[SentMessages],
				[ReceivedMessages],
				[ConnectedGatewayCount]
			)
			VALUES
			(
				@deploymentId,
				@clientId,
				GETUTCDATE(),
				@address,
				@hostName,
				@cpuUsage,
				@memoryUsage,
				@sendQueueLength,
				@receiveQueueLength,
				@sentMessagesCount,
				@receivedMessagesCount,
				@connectedGatewaysCount
			);
		END
		COMMIT TRANSACTION;',
		N''
	);

	INSERT INTO [OrleansQuery]([Key], [Query], [Description])
	VALUES
	(
		'UpsertSiloMetricsKey',
		N'SET XACT_ABORT, NOCOUNT ON;		
		BEGIN TRANSACTION;
		IF EXISTS(SELECT 1 FROM  [OrleansSiloMetricsTable]  WITH(updlock, holdlock) WHERE
			[DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL
			AND [SiloId] = @siloId AND @siloId IS NOT NULL)
		BEGIN
			UPDATE [OrleansSiloMetricsTable]
			SET
				Timestamp = GETUTCDATE(),
				Address = @address,
				Port = @port,
				Generation = @generation,
				HostName = @hostName,
				GatewayAddress = @gatewayAddress,
				GatewayPort = @gatewayPort,
				CPU = @cpuUsage,
				Memory = @memoryUsage,
				Activations = @activationsCount,
				RecentlyUsedActivations = @recentlyUsedActivationsCount,
				SendQueue = @sendQueueLength,
				ReceiveQueue = @receiveQueueLength,
				RequestQueue = @requestQueueLength,
				SentMessages = @sentMessagesCount,
				ReceivedMessages = @receivedMessagesCount,
				LoadShedding = @isOverloaded,
				ClientCount = @clientCount;
		END
		ELSE
		BEGIN
			INSERT INTO [OrleansSiloMetricsTable]
			(
				[DeploymentId],
				[SiloId],
				[Timestamp],
				[Address],
				[Port],
				[Generation],
				[HostName],
				[GatewayAddress],
				[GatewayPort],
				[CPU],
				[Memory],
				[Activations],
				[RecentlyUsedActivations],
				[SendQueue],
				[ReceiveQueue],
				[RequestQueue],
				[SentMessages],	
				[ReceivedMessages],
				[LoadShedding],
				[ClientCount]
			)
			VALUES
			(
				@deploymentId,
				@siloId,
				GETUTCDATE(),
				@address,
				@port,
				@generation,
				@hostName,
				@gatewayAddress,
				@gatewayPort,
				@cpuUsage,
				@memoryUsage,
				@activationsCount,
				@recentlyUsedActivationsCount,
				@sendQueueLength,
				@receiveQueueLength,
				@requestQueueLength,
				@sentMessagesCount,
				@receivedMessagesCount,
				@isOverloaded,
				@clientCount
			);
		END
		COMMIT TRANSACTION;',
		N''
	);
END

INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'ActiveGatewaysQueryKey',
	N'SET NOCOUNT ON;
	SELECT
		[Address],
        [ProxyPort]
      FROM
		[MembershipReadAll]
      WHERE
		[DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL
        AND [Status]   = @status AND @status IS NOT NULL;',
	N''
);

INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'MembershipReadRowKey',
	N'SET NOCOUNT ON;
	SELECT
		v.[DeploymentId],
		m.[Address],
		m.[Port],
		m.[Generation],
		m.[HostName],
		m.[Status],
		m.[ProxyPort],
		m.[Primary],
		m.[RoleName],
		m.[InstanceName],
		m.[UpdateZone],
		m.[FaultZone],
		m.[SuspectingSilos],
		m.[SuspectingTimes],
		m.[StartTime],
		m.[IAmAliveTime],
		m.[ETag],
		v.[Version],
		v.[ETag] AS VersionETag
	FROM
		[OrleansMembershipVersionTable] v
		-- This ensures the version table will returned even if there is no matching membership row.
		LEFT OUTER JOIN [OrleansMembershipTable] m ON v.DeploymentId = m.DeploymentId AND @deploymentId IS NOT NULL	
		AND ([Address] = @address AND @address IS NOT NULL)
		AND ([Port]    = @port AND @port IS NOT NULL)
		AND ([Generation] = @generation AND @generation IS NOT NULL);',
	N''
);

INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'MembershipReadAllKey',
	N'SET NOCOUNT ON;
	SELECT
		[Port],
		[Generation],
		[Address],
		[HostName],
		[Status],
		[ProxyPort],
		[Primary],
		[RoleName],
		[InstanceName],
		[UpdateZone],
		[FaultZone],
		[StartTime],
		[IAmAliveTime],
		[ETag],
		[Version],
		[VersionETag],
		[SuspectingSilos],
		[SuspectingTimes]
	FROM
		[MembershipReadAll]
	WHERE
		[DeploymentId]   = @deploymentId AND @deploymentId IS NOT NULL;',
	N''
);

INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'DeleteMembershipTableEntriesKey',
	N'SET XACT_ABORT, NOCOUNT ON;
    BEGIN TRANSACTION;                                        
    DELETE FROM [OrleansMembershipTable]
    WHERE [DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL;

    DELETE FROM [OrleansMembershipVersionTable]
    WHERE [DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL;
    COMMIT TRANSACTION;',
	N''
);

INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'ReadReminderRowsKey',
	N'SET NOCOUNT ON;
    SELECT
		[GrainId],
		[ReminderName],
		[StartTime],
		[Period],
		[ETag]
	FROM [OrleansRemindersTable]
	WHERE
		[ServiceId] = @serviceId AND @serviceId IS NOT NULL
		AND [GrainId] = @grainId AND @grainId IS NOT NULL;',
	N''
);

INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'ReadReminderRowKey',
	N'SET NOCOUNT ON;
    SELECT
        [GrainId],
        [ReminderName],
        [StartTime],
        [Period],
        [ETag]
    FROM [OrleansRemindersTable]
    WHERE
        [ServiceId] = @serviceId AND @serviceId IS NOT NULL
        AND [GrainId] = @grainId AND @grainId IS NOT NULL
        AND [ReminderName] = @reminderName AND @reminderName IS NOT NULL;',
	N''
);

INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'ReadRangeRows1Key',
	N'SET NOCOUNT ON;
		SELECT
		[GrainId],
		[ReminderName],
		[StartTime],
		[Period],
		[ETag]
	FROM [OrleansRemindersTable]
	WHERE
		[ServiceId] = @serviceId AND @serviceId IS NOT NULL
		AND ([GrainIdConsistentHash] > @beginHash AND @beginHash IS NOT NULL
				AND [GrainIdConsistentHash] <= @endHash AND @endHash IS NOT NULL);',
	N''
);

INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'ReadRangeRows2Key',
	N'SET NOCOUNT ON;
		SELECT
		[GrainId],
		[ReminderName],
		[StartTime],
		[Period],
		[ETag]
	FROM [OrleansRemindersTable]
	WHERE
		[ServiceId] = @serviceId AND @serviceId IS NOT NULL
		AND ([GrainIdConsistentHash] > @beginHash AND @beginHash IS NOT NULL
				OR [GrainIdConsistentHash] <= @endHash AND @endHash IS NOT NULL);',
	N''
);


INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'InsertOrleansStatisticsKey',
	N'SET XACT_ABORT, NOCOUNT ON;
	  BEGIN TRANSACTION;
		INSERT INTO [OrleansStatisticsTable]
		(
			[DeploymentId],
			[Id],
			[HostName],
			[Name],
			[IsDelta],
			[StatValue],
			[Statistic]
		)
		SELECT
			@deploymentId,
			@id,
			@hostName,
			@name,
			@isDelta,
			@statValue,
			@statistic;
		COMMIT TRANSACTION;',
	N''
);


INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'DeleteReminderRowKey',
	N'SET XACT_ABORT, NOCOUNT ON;
		DECLARE @rowsDeleted AS INT = 0;
		BEGIN TRANSACTION;      
		DELETE FROM [OrleansRemindersTable]
		WHERE
			[ServiceId] = @serviceId AND @serviceId IS NOT NULL
			AND [GrainId] = @grainId AND @grainId IS NOT NULL
			AND [ReminderName] = @reminderName AND @reminderName IS NOT NULL
			AND ETag = @etag AND @etag IS NOT NULL
		SET @rowsDeleted = @@ROWCOUNT;		
		COMMIT TRANSACTION;
		SELECT CAST(@rowsDeleted AS BIT);',
	N'');

INSERT INTO [OrleansQuery]([Key], [Query], [Description])
VALUES
(
	'DeleteReminderRowsKey',
	N'SET XACT_ABORT, NOCOUNT ON;
	  BEGIN TRANSACTION;
	  DELETE FROM [OrleansRemindersTable]
	  WHERE
	      [ServiceId] = @serviceId AND @serviceId IS NOT NULL;
	 COMMIT TRANSACTION;',
	N''
);

GO

CREATE VIEW [MembershipReadAll] AS
SELECT
    v.[DeploymentId],
    m.[Address],
    m.[Port],
    m.[Generation],
    m.[HostName],
    m.[Status],
    m.[ProxyPort],
    m.[Primary],
    m.[RoleName],
    m.[InstanceName],
    m.[UpdateZone],
    m.[FaultZone],
    m.[SuspectingSilos],
    m.[SuspectingTimes],
    m.[StartTime],
    m.[IAmAliveTime],
    m.[ETag],
    v.[Version],
    v.[ETag] AS VersionETag
FROM
    [dbo].[OrleansMembershipVersionTable] v
    LEFT OUTER JOIN [dbo].[OrleansMembershipTable] m ON v.DeploymentId = m.DeploymentId;

GO
