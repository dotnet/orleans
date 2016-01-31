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
   VARBINARY(n) when querying. The type of its actual implementation is not important as long as it represents a unique version.

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
		http://dotnet.github.io/orleans/Runtime-Implementation-Details/Cluster-Management
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
            AND RIGHT(LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), 5), 2) = '0.' THEN 'SQL Server 2008'
        WHEN LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), CHARINDEX('.', CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR)) - 1) = 10 
            AND RIGHT(LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), 5), 2) = '50' THEN 'SQL Server 2008 R2' 
        WHEN LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), CHARINDEX('.', CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR)) - 1) = 11 THEN 'SQL Server 2012'
        WHEN LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), CHARINDEX('.', CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR)) - 1) = 12 THEN 'SQL Server 2014'
		WHEN LEFT(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR), CHARINDEX('.', CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR)) - 1) = 13 THEN 'SQL Server 2016' 				
    END AS [Value],
    N'The database product name.' AS [Description]
UNION ALL
SELECT
    N'Database version' AS [Id], 
    CAST(SERVERPROPERTY('productversion') AS NVARCHAR) AS [Value],
    N'The version number of the database.' AS [Description];

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
-- These can be redefined (e.g. to provide non-destructive updates) provided the stated interface principles hold.
CREATE TABLE [OrleansQuery]
(	
    [QueryKey] VARCHAR(64) NOT NULL,
    [QueryText] NVARCHAR(MAX) NOT NULL

	CONSTRAINT OrleansQuery_Key PRIMARY KEY([QueryKey])
);


-- There will ever be only one (active) membership version table version column which will be updated periodically.
-- See table description at http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html. The following
-- IF-ELSE does SQL Server version detection and defines separate table structures and queries for them.
-- Orleans issues the queries as defined in [OrleansQuery] and operates through parameter names and types with no
-- regard to other matters.
--
-- This is only an optimization to use features in newer database editions and not strictly necessary for proper functioning of Orleans.
IF(NOT EXISTS(SELECT [Value] FROM [OrleansDatabaseInfo] WHERE Id = N'ProductName' AND [Value] IN (N'SQL Server 2000')))
BEGIN
	-- These table definitions are SQL Server 2005 and later. The differences are
	-- the ETag is ROWVersion in SQL Server 2005 and later whereas in SQL Server 2000 UNIQUEIDENTIFIER is used
	-- and SQL Server 2005 and later use DATETIME2 and associated functions whereas SQL Server uses DATETIME.
	CREATE TABLE [OrleansMembershipVersionTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[Timestamp] DATETIME2(3) NOT NULL DEFAULT GETUTCDATE(), 
		[Version] BIGINT NOT NULL DEFAULT 0,
    
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
		[RoleName] NVARCHAR(150) NULL, 
		[InstanceName] NVARCHAR(150) NULL, 
		[UpdateZone] INT NULL, 
		[FaultZone] INT NULL,		
		[SuspectingSilos] NVARCHAR(MAX) NULL, 
		[SuspectingTimes] NVARCHAR(MAX) NULL, 
		[StartTime] DATETIME2(3) NOT NULL, 
		[IAmAliveTime] DATETIME2(3) NOT NULL,			
		
		-- Not using ROWVERSION here since we're not updating the ETag on IAmAliveTime field updates.
		-- Using BINARY(8) because a nonnullable ROWVERSION column is semantically equivalent to a binary(8) column. 
		[ETag] BINARY(8) NOT NULL DEFAULT 0,
		    
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
		[StartTime] DATETIME2(3) NOT NULL, 
		[Period] INT NOT NULL,
		[GrainIdConsistentHash] INT NOT NULL,
		[ETag] ROWVERSION NOT NULL,
    
		CONSTRAINT PK_OrleansRemindersTable_ServiceId_GrainId_ReminderName PRIMARY KEY([ServiceId], [GrainId], [ReminderName])
	);

	CREATE TABLE [OrleansStatisticsTable]
	(
		[OrleansStatisticsTableId] INT IDENTITY(1,1) NOT NULL,
		[DeploymentId] NVARCHAR(150) NOT NULL,      
		[Timestamp] DATETIME2(3) NOT NULL DEFAULT GETUTCDATE(), 
		[Id] NVARCHAR(250) NOT NULL,     
		[HostName] NVARCHAR(150) NOT NULL, 
		[Name] NVARCHAR(150) NULL, 
		[IsDelta] BIT NOT NULL, 
		[StatValue] NVARCHAR(1024) NOT NULL,
		[Statistic] NVARCHAR(250) NOT NULL,

		CONSTRAINT OrleansStatisticsTable_OrleansStatisticsTableId PRIMARY KEY([OrleansStatisticsTableId])	
	);
	
	CREATE TABLE [OrleansClientMetricsTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[ClientId] NVARCHAR(150) NOT NULL, 
		[Timestamp] DATETIME2(3) NOT NULL DEFAULT GETUTCDATE(),
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
		[Timestamp] DATETIME2(3) NOT NULL DEFAULT GETUTCDATE(),
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

	-- Some of the Orleans queries are version specific due to an optimization to use ROWVERSION on SQL Server 2005 and later.
	-- ROWVERSION is applied automatically whereas an etag mechanism of using UNIQUEIDENTIFIER in SQL Server is not.
	-- Also some queries could be tuned better on SQL Server 2005 and later such as error handling or SQL Server 2008
	-- and later using MERGE for UPSERT (reminders).
	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
		COMMIT TRANSACTION;'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
	VALUES
	(
		-- There should ever be only one version row. A new one is tried to insert only once when a silo starts.
		-- The concurrency is virtually non-existent, but for the sake robustness, appropriate locks are taken.
		'InsertMembershipVersionKey',
		N'SET NOCOUNT ON;
		BEGIN TRANSACTION;		
		INSERT INTO [OrleansMembershipVersionTable]
		(
			[DeploymentId] 
		)
		SELECT	
			@deploymentId   
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
		END'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
				[Timestamp]	= GETUTCDATE(),
				[Version]	= @versionEtag + 1
			WHERE
				([DeploymentId]	= @deploymentId AND @deploymentId IS NOT NULL)
				AND ([Version]		= @versionEtag AND @versionEtag IS NOT NULL);

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
		END'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
			[RoleName]			= @roleName,
			[InstanceName]		= @instanceName,
			[UpdateZone]		= @updateZone,
			[FaultZone]			= @faultZone,
			[SuspectingSilos]	= @suspectingSilos,
			[SuspectingTimes]	= @suspectingTimes,
			[StartTime]			= @startTime,
			[IAmAliveTime]		= @iAmAliveTime,
			[Etag]				= @etag + 1
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
				[Version]	= @versionEtag + 1
			WHERE
				DeploymentId	= @deploymentId AND @deploymentId IS NOT NULL
				AND Version		= @versionEtag AND @versionEtag IS NOT NULL;

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
		END'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
				AND [ReminderName] = @reminderName AND @reminderName IS NOT NULL;
			END	
			COMMIT TRANSACTION;'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
			WHERE
				([DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL)
				AND ([ClientId] = @clientId AND @clientId IS NOT NULL);
		END
		ELSE
		BEGIN	
			INSERT INTO [OrleansClientMetricsTable]
			(
				[DeploymentId],
				[ClientId],
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
		COMMIT TRANSACTION;'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
				ClientCount = @clientCount
			WHERE
				([DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL)
				AND ([SiloId] = @siloId AND @siloId IS NOT NULL);
		END
		ELSE
		BEGIN
			INSERT INTO [OrleansSiloMetricsTable]
			(
				[DeploymentId],
				[SiloId],
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
		COMMIT TRANSACTION;'
	);
END
ELSE
BEGIN
	-- These table definitions are for SQL Server 2000.
	CREATE TABLE [OrleansMembershipVersionTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[Timestamp] DATETIME NOT NULL DEFAULT GETUTCDATE(),
		[Version] BIGINT NOT NULL DEFAULT 0,
    
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
		[RoleName] NVARCHAR(150) NULL, 
		[InstanceName] NVARCHAR(150) NULL, 
		[UpdateZone] INT NULL, 
		[FaultZone] INT NULL,		
		[SuspectingSilos] NTEXT NULL, 
		[SuspectingTimes] NTEXT NULL, 
		[StartTime] DATETIME NOT NULL, 
		[IAmAliveTime] DATETIME NOT NULL,
	
		-- This ETag should always be unique for a given primary key.
		-- This is BINARY(8) to be as much compatible with the later SQL Server
		-- editions as possible. 
		[ETag] BINARY(8) NOT NULL DEFAULT 0,
    
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
				
		-- This is BINARY(8) to be as much compatible with the later SQL Server
		-- editions as possible.
		[ETag] BINARY(8) NOT NULL DEFAULT 0,
    
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
		[StatValue] NVARCHAR(1024) NOT NULL,
		[Statistic] NVARCHAR(250) NOT NULL,

		CONSTRAINT OrleansStatisticsTable_OrleansStatisticsTableId PRIMARY KEY([OrleansStatisticsTableId])	
	);

	CREATE TABLE [OrleansClientMetricsTable]
	(
		[DeploymentId] NVARCHAR(150) NOT NULL, 
		[ClientId] NVARCHAR(150) NOT NULL, 
		[Timestamp] DATETIME NOT NULL DEFAULT GETUTCDATE(),
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
		[Timestamp] DATETIME NOT NULL DEFAULT GETUTCDATE(),
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

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
		COMMIT TRANSACTION;'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
	VALUES
	(
		'InsertMembershipVersionKey',
		N'SET NOCOUNT ON;
		BEGIN TRANSACTION;
		INSERT INTO [OrleansMembershipVersionTable]
		(
			[DeploymentId]
		)
		SELECT	
			@deploymentId
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
		END'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
				[Timestamp]	= GETUTCDATE(),
				[Version]	= @versionEtag + 1
			WHERE
				([DeploymentId]	= @deploymentId AND @deploymentId IS NOT NULL)
				AND ([Version]		= @versionEtag AND @versionEtag IS NOT NULL);

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
		END'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
			[RoleName]			= @roleName,
			[InstanceName]		= @instanceName,
			[UpdateZone]		= @updateZone,
			[FaultZone]			= @faultZone,
			[SuspectingSilos]	= @suspectingSilos,
			[SuspectingTimes]	= @suspectingTimes,
			[StartTime]			= @startTime,
			[IAmAliveTime]		= @iAmAliveTime,
			[ETag]				= @etag + 1
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
				[Version]	= @versionEtag + 1
			WHERE
				DeploymentId	= @deploymentId AND @deploymentId IS NOT NULL
				AND Version		= @versionEtag AND @versionEtag IS NOT NULL;

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
		END'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
	VALUES
	(
		'UpsertReminderRowKey',
		N'SET XACT_ABORT, NOCOUNT ON;
		DECLARE @newEtag AS BINARY(8) = 0;
		BEGIN TRANSACTION;
		SELECT @newEtag = [ETag] + 1 FROM [OrleansRemindersTable]  WITH(updlock, holdlock) WHERE
			[ServiceId] = @serviceId AND @serviceId IS NOT NULL
			AND [GrainId] = @grainId AND @grainId IS NOT NULL
			AND [ReminderName] = @reminderName AND @reminderName IS NOT NULL;
		IF @newEtag = 0
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
				[GrainIdConsistentHash] = @grainIdConsistentHash,
				[ETag]                  = @newEtag
			WHERE
				[ServiceId] = @serviceId AND @serviceId IS NOT NULL
				AND [GrainId] = @grainId AND @grainId IS NOT NULL
				AND [ReminderName] = @reminderName AND @reminderName IS NOT NULL;
		END	
		COMMIT TRANSACTION;
		SELECT @newEtag AS ETag;'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
			WHERE
				([DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL)
				AND ([ClientId] = @clientId AND @clientId IS NOT NULL);
		END
		ELSE
		BEGIN	
			INSERT INTO [OrleansClientMetricsTable]
			(
				[DeploymentId],
				[ClientId],
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
		COMMIT TRANSACTION;'
	);

	INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
				ClientCount = @clientCount
			WHERE
				([DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL)
				AND ([SiloId] = @siloId AND @siloId IS NOT NULL);
		END
		ELSE
		BEGIN
			INSERT INTO [OrleansSiloMetricsTable]
			(
				[DeploymentId],
				[SiloId],
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
		COMMIT TRANSACTION;'
	);
END

INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
VALUES
(
	'ActiveGatewaysQueryKey',
	N'SET NOCOUNT ON;
	SELECT
		[Address],
		[ProxyPort],
		[Generation]
	FROM
		[OrleansMembershipTable]
	WHERE
		[DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL
		AND [Status]   = @status AND @status IS NOT NULL;'
);

INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
		m.[RoleName],
		m.[InstanceName],
		m.[UpdateZone],
		m.[FaultZone],
		m.[SuspectingSilos],
		m.[SuspectingTimes],
		m.[StartTime],
		m.[IAmAliveTime],
		m.[ETag],
		v.[Version]
	FROM
		[OrleansMembershipVersionTable] v
		-- This ensures the version table will returned even if there is no matching membership row.
		LEFT OUTER JOIN [OrleansMembershipTable] m ON v.[DeploymentId] = m.[DeploymentId]	
		AND ([Address] = @address AND @address IS NOT NULL)
		AND ([Port]    = @port AND @port IS NOT NULL)
		AND ([Generation] = @generation AND @generation IS NOT NULL)
		WHERE v.[DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL;'
);

INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
VALUES
(
	'MembershipReadAllKey',
	N'SET NOCOUNT ON;
	SELECT
		v.[DeploymentId],
		m.[Address],
		m.[Port],
		m.[Generation],
		m.[HostName],
		m.[Status],
		m.[ProxyPort],
		m.[RoleName],
		m.[InstanceName],
		m.[UpdateZone],
		m.[FaultZone],
		m.[SuspectingSilos],
		m.[SuspectingTimes],
		m.[StartTime],
		m.[IAmAliveTime],
		m.[ETag],
		v.[Version]
	FROM
		[OrleansMembershipVersionTable] v
		LEFT OUTER JOIN [OrleansMembershipTable] m ON v.[DeploymentId] = m.[DeploymentId]
	WHERE
		v.[DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL;'
);

INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
VALUES
(
	'DeleteMembershipTableEntriesKey',
	N'SET XACT_ABORT, NOCOUNT ON;
    BEGIN TRANSACTION;                                        
    DELETE FROM [OrleansMembershipTable]
    WHERE [DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL;

    DELETE FROM [OrleansMembershipVersionTable]
    WHERE [DeploymentId] = @deploymentId AND @deploymentId IS NOT NULL;
    COMMIT TRANSACTION;'
);

INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
		AND [GrainId] = @grainId AND @grainId IS NOT NULL;'
);

INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
        AND [ReminderName] = @reminderName AND @reminderName IS NOT NULL;'
);

INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
				AND [GrainIdConsistentHash] <= @endHash AND @endHash IS NOT NULL);'
);

INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
				OR [GrainIdConsistentHash] <= @endHash AND @endHash IS NOT NULL);'
);


INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
		COMMIT TRANSACTION;'
);


INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
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
		SELECT CAST(@rowsDeleted AS BIT);'
	);

INSERT INTO [OrleansQuery]([QueryKey], [QueryText])
VALUES
(
	'DeleteReminderRowsKey',
	N'SET XACT_ABORT, NOCOUNT ON;
	  BEGIN TRANSACTION;
	  DELETE FROM [OrleansRemindersTable]
	  WHERE
	      [ServiceId] = @serviceId AND @serviceId IS NOT NULL;
	 COMMIT TRANSACTION;'
);

GO
