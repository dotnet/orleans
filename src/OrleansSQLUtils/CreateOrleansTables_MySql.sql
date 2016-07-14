/*
Implementation notes:

1) The general idea is that data is read and written through Orleans specific queries.
   Orleans operates on column names and types when reading and on parameter names and types when writing.
   
2) The implementations *must* preserve input and output names and types. Orleans uses these parameters to reads query results by name and type.
   Vendor and deployment specific tuning is allowed and contributions are encouraged as long as the interface contract
   is maintained.
	 
3) The implementation across vendor specific scripts *should* preserve the constraint names. This simplifies troubleshooting
   by virtue of uniform naming across concrete implementations.

5) ETag for Orleans is an opaque column that represents a unique version. The type of its actual implementation
   is not important as long as it represents a unique version. In this implementation we use integers for versioning

6) For the sake of being explicit and removing ambiguity, Orleans expects some queries to return either TRUE as >0 value 
   or FALSE as =0 value. That is, affected rows or such does not matter. If an error is raised or an exception is thrown
   the query *must* ensure the entire transaction is rolled back and may either return FALSE or propagate the exception.
   Orleans handles exception as a failure and will retry.

7) The implementation follows the Extended Orleans membership protocol. For more information, see at:
		http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html
		http://dotnet.github.io/orleans/Runtime-Implementation-Details/Cluster-Management
		https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs
*/
-- This table defines Orleans operational queries. Orleans uses these to manage its operations,
-- these are the only queries Orleans issues to the database.
-- These can be redefined (e.g. to provide non-destructive updates) provided the stated interface principles hold.
CREATE TABLE OrleansQuery
(
	QueryKey VARCHAR(64) NOT NULL,
	QueryText VARCHAR(8000) NOT NULL,

	CONSTRAINT OrleansQuery_Key PRIMARY KEY(QueryKey)
);

-- For each deployment, there will be only one (active) membership version table version column which will be updated periodically.
CREATE TABLE OrleansMembershipVersionTable
(
	DeploymentId NVARCHAR(150) NOT NULL,
	Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	Version INT NOT NULL DEFAULT 0,

	CONSTRAINT PK_OrleansMembershipVersionTable_DeploymentId PRIMARY KEY(DeploymentId)
);

-- Every silo instance has a row in the membership table.
CREATE TABLE OrleansMembershipTable
(
	DeploymentId NVARCHAR(150) NOT NULL,
	Address VARCHAR(45) NOT NULL,
	Port INT NOT NULL,
	Generation INT NOT NULL,
	SiloName NVARCHAR(150) NOT NULL,
	HostName NVARCHAR(150) NOT NULL,
	Status INT NOT NULL,
	ProxyPort INT NULL,
	SuspectTimes VARCHAR(8000) NULL,
	StartTime DATETIME NOT NULL,
	IAmAliveTime DATETIME NOT NULL,	
	
	CONSTRAINT PK_MembershipTable_DeploymentId PRIMARY KEY(DeploymentId, Address, Port, Generation),
	CONSTRAINT FK_MembershipTable_MembershipVersionTable_DeploymentId FOREIGN KEY (DeploymentId) REFERENCES OrleansMembershipVersionTable (DeploymentId)
);

-- Orleans Reminders table - http://dotnet.github.io/orleans/Advanced-Concepts/Timers-and-Reminders
CREATE TABLE OrleansRemindersTable
(
	ServiceId NVARCHAR(150) NOT NULL,
	GrainId VARCHAR(150) NOT NULL,
	ReminderName NVARCHAR(150) NOT NULL,
	StartTime DATETIME NOT NULL,
	Period INT NOT NULL,
	GrainHash INT NOT NULL,
	Version INT NOT NULL,

	CONSTRAINT PK_RemindersTable_ServiceId_GrainId_ReminderName PRIMARY KEY(ServiceId, GrainId, ReminderName)
);

CREATE TABLE OrleansStatisticsTable
(
	OrleansStatisticsTableId INT NOT NULL AUTO_INCREMENT,
	DeploymentId NVARCHAR(150) NOT NULL,
	Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	Id NVARCHAR(250) NOT NULL,
	HostName NVARCHAR(150) NOT NULL,
	Name NVARCHAR(150) NOT NULL,
	IsValueDelta BIT NOT NULL,
	StatValue NVARCHAR(1024) NOT NULL,
	Statistic NVARCHAR(512) NOT NULL,

	CONSTRAINT StatisticsTable_StatisticsTableId PRIMARY KEY(OrleansStatisticsTableId)
);

CREATE TABLE OrleansClientMetricsTable
(
	DeploymentId NVARCHAR(150) NOT NULL,
	ClientId NVARCHAR(150) NOT NULL,
	Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	Address VARCHAR(45) NOT NULL,
	HostName NVARCHAR(150) NOT NULL,
	CpuUsage FLOAT NOT NULL,
	MemoryUsage BIGINT NOT NULL,
	SendQueueLength INT NOT NULL,
	ReceiveQueueLength INT NOT NULL,
	SentMessages BIGINT NOT NULL,
	ReceivedMessages BIGINT NOT NULL,
	ConnectedGatewayCount BIGINT NOT NULL,

	CONSTRAINT PK_ClientMetricsTable_DeploymentId_ClientId PRIMARY KEY (DeploymentId , ClientId)
);

CREATE TABLE OrleansSiloMetricsTable
(
	DeploymentId NVARCHAR(150) NOT NULL,
	SiloId NVARCHAR(150) NOT NULL,
	Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	Address VARCHAR(45) NOT NULL,
	Port INT NOT NULL,
	Generation INT NOT NULL,
	HostName NVARCHAR(150) NOT NULL,
	GatewayAddress VARCHAR(45) NOT NULL,
	GatewayPort INT NOT NULL,
	CpuUsage FLOAT NOT NULL,
	MemoryUsage BIGINT NOT NULL,
	SendQueueLength INT NOT NULL,
	ReceiveQueueLength INT NOT NULL,
	SentMessages BIGINT NOT NULL,
	ReceivedMessages BIGINT NOT NULL,
	ActivationCount INT NOT NULL,
	RecentlyUsedActivationCount INT NOT NULL,
	RequestQueueLength BIGINT NOT NULL,
	IsOverloaded BIT NOT NULL,
	ClientCount BIGINT NOT NULL,

	CONSTRAINT PK_SiloMetricsTable_DeploymentId_SiloId PRIMARY KEY (DeploymentId , SiloId),
	CONSTRAINT FK_SiloMetricsTable_MembershipVersionTable_DeploymentId FOREIGN KEY (DeploymentId) REFERENCES OrleansMembershipVersionTable (DeploymentId)
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpdateIAmAlivetimeKey','
	-- This is expected to never fail by Orleans, so return value
	-- is not needed nor is it checked.
	UPDATE OrleansMembershipTable
	SET
		IAmAliveTime = @IAmAliveTime
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertMembershipVersionKey','
	INSERT INTO OrleansMembershipVersionTable
	(
		DeploymentId
	)
	SELECT @DeploymentId
	WHERE NOT EXISTS
	(
	SELECT 1
	FROM
		OrleansMembershipVersionTable
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
	);

	SELECT ROW_COUNT();
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertMembershipKey','
	call InsertMembershipKey(@DeploymentId, @Address, @Port, @Generation, 
	@Version, @SiloName, @HostName, @Status, @ProxyPort, @StartTime, @IAmAliveTime);'
);

DELIMITER $$

CREATE PROCEDURE InsertMembershipKey(
	in	_DeploymentId NVARCHAR(150),
	in	_Address VARCHAR(45),
	in	_Port INT,
	in	_Generation INT,
	in	_Version INT,
	in	_SiloName NVARCHAR(150),
	in	_HostName NVARCHAR(150),
	in	_Status INT,
	in	_ProxyPort INT,
	in	_StartTime DATETIME,
	in	_IAmAliveTime DATETIME
)
BEGIN
	DECLARE _ROWCOUNT INT;
	START TRANSACTION;
	INSERT INTO OrleansMembershipTable
	(
		DeploymentId,
		Address,
		Port,
		Generation,
		SiloName,
		HostName,
		Status,
		ProxyPort,
		StartTime,
		IAmAliveTime
	)
	SELECT 
		_DeploymentId,
		_Address,
		_Port,
		_Generation,
		_SiloName,
		_HostName,
		_Status,
		_ProxyPort,
		_StartTime,
		_IAmAliveTime
	WHERE NOT EXISTS
	(
	SELECT 1
	FROM
		OrleansMembershipTable
	WHERE 
		DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
		AND Address = _Address AND _Address IS NOT NULL
		AND Port = _Port AND _Port IS NOT NULL
		AND Generation = _Generation AND _Generation IS NOT NULL
	);

	UPDATE OrleansMembershipVersionTable
	SET
		Version = Version + 1
	WHERE
		DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
		AND Version = _Version AND _Version IS NOT NULL
		AND ROW_COUNT() > 0;

	SET _ROWCOUNT = ROW_COUNT();

	IF _ROWCOUNT = 0
	THEN 
		ROLLBACK;
	ELSE
		COMMIT;
	END IF;
	SELECT _ROWCOUNT;
END$$

DELIMITER ;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpdateMembershipKey','
	START TRANSACTION;

	UPDATE OrleansMembershipVersionTable
	SET
		Version = Version + 1
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Version = @Version AND @Version IS NOT NULL;

	UPDATE OrleansMembershipTable
	SET
		Status = @Status,
		SuspectTimes = @SuspectTimes,
		IAmAliveTime = @IAmAliveTime
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL
		AND ROW_COUNT() > 0;

	SELECT ROW_COUNT();
	COMMIT;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertReminderRowKey','
	INSERT INTO OrleansRemindersTable
	(
		ServiceId,
		GrainId,
		ReminderName,
		StartTime,
		Period,
		GrainHash,
		Version
	)
	VALUES
	(
		@ServiceId,
		@GrainId,
		@ReminderName,
		@StartTime,
		@Period,
		@GrainHash,
		last_insert_id(0)
	)
	ON DUPLICATE KEY
	UPDATE
		StartTime = @StartTime,
		Period = @Period,
		GrainHash = @GrainHash,
		Version = last_insert_id(Version+1);
	
	
	SELECT last_insert_id() AS Version;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertReportClientMetricsKey','
	INSERT INTO OrleansClientMetricsTable
	(
		DeploymentId,
		ClientId,
		Address,			
		HostName,
		CpuUsage,
		MemoryUsage,
		SendQueueLength,
		ReceiveQueueLength,
		SentMessages,
		ReceivedMessages,
		ConnectedGatewayCount
	)
	VALUES
	(
		@DeploymentId,
		@ClientId,
		@Address,
		@HostName,
		@CpuUsage,
		@MemoryUsage,
		@SendQueueLength,
		@ReceiveQueueLength,
		@SentMessages,
		@ReceivedMessages,
		@ConnectedGatewayCount
	)
	ON DUPLICATE KEY 
	UPDATE 
		Address = @Address,
		HostName = @HostName,
		CpuUsage = @CpuUsage,
		MemoryUsage = @MemoryUsage,
		SendQueueLength = @SendQueueLength,
		ReceiveQueueLength = @ReceiveQueueLength,
		SentMessages = @SentMessages,
		ReceivedMessages = @ReceivedMessages,
		ConnectedGatewayCount = @ConnectedGatewayCount;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertSiloMetricsKey','
	INSERT INTO OrleansSiloMetricsTable
	(
		DeploymentId,
		SiloId,
		Address,
		Port,
		Generation,
		HostName,
		GatewayAddress,
		GatewayPort,
		CpuUsage,
		MemoryUsage,
		SendQueueLength,
		ReceiveQueueLength,
		SentMessages,	
		ReceivedMessages,
		ActivationCount,
		RecentlyUsedActivationCount,
		RequestQueueLength,
		IsOverloaded,
		ClientCount
	)
	VALUES
	(
		@DeploymentId,
		@SiloId,
		@Address,
		@Port,
		@Generation,
		@HostName,
		@GatewayAddress,
		@GatewayPort,
		@CpuUsage,
		@MemoryUsage,
		@SendQueueLength,
		@ReceiveQueueLength,
		@SentMessages,	
		@ReceivedMessages,
		@ActivationCount,
		@RecentlyUsedActivationCount,
		@RequestQueueLength,
		@IsOverloaded,
		@ClientCount
	)
	ON DUPLICATE KEY 
	UPDATE 
		Address = @Address,
		Port = @Port,
		Generation = @Generation,
		HostName = @HostName,
		GatewayAddress = @GatewayAddress,
		GatewayPort= @GatewayPort,
		CpuUsage = @CpuUsage,
		MemoryUsage = @MemoryUsage,
		SendQueueLength = @SendQueueLength,
		ReceiveQueueLength = @ReceiveQueueLength,
		SentMessages = @SentMessages,
		ReceivedMessages = @ReceivedMessages,
		ActivationCount = @ActivationCount,
		RecentlyUsedActivationCount = @RecentlyUsedActivationCount,
		RequestQueueLength = @RequestQueueLength,
		IsOverloaded = @IsOverloaded,
		ClientCount = @ClientCount;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'GatewaysQueryKey','
	SELECT
		Address,
		ProxyPort,
		Generation
	FROM
		OrleansMembershipTable
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Status = @Status AND @Status IS NOT NULL
		AND ProxyPort > 0;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'MembershipReadRowKey','
	SELECT
		v.DeploymentId,
		m.Address,
		m.Port,
		m.Generation,
		m.SiloName,
		m.HostName,
		m.Status,
		m.ProxyPort,
		m.SuspectTimes,
		m.StartTime,
		m.IAmAliveTime,
		v.Version
	FROM
		OrleansMembershipVersionTable v
		-- This ensures the version table will returned even if there is no matching membership row.
		LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId	
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL
	WHERE 
		v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'MembershipReadAllKey','
	SELECT
		v.DeploymentId,
		m.Address,
		m.Port,
		m.Generation,
		m.SiloName,
		m.HostName,
		m.Status,
		m.ProxyPort,
		m.SuspectTimes,
		m.StartTime,
		m.IAmAliveTime,
		v.Version
	FROM
		OrleansMembershipVersionTable v LEFT OUTER JOIN OrleansMembershipTable m
		ON v.DeploymentId = m.DeploymentId
	WHERE
		v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteMembershipTableEntriesKey','
	DELETE FROM OrleansMembershipTable
	WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
	DELETE FROM OrleansMembershipVersionTable
	WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadReminderRowsKey','
	SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		Version
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND GrainId = @GrainId AND @GrainId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadReminderRowKey','
	SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		Version
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND GrainId = @GrainId AND @GrainId IS NOT NULL
		AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadRangeRows1Key','
	SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		Version
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND GrainHash > @BeginHash AND @BeginHash IS NOT NULL
		AND GrainHash <= @EndHash AND @EndHash IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadRangeRows2Key','
	SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		Version
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND ((GrainHash > @BeginHash AND @BeginHash IS NOT NULL)
		OR (GrainHash <= @EndHash AND @EndHash IS NOT NULL));
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertOrleansStatisticsKey','
	START TRANSACTION;
	INSERT INTO OrleansStatisticsTable
	(
		DeploymentId,
		Id,
		HostName,
		Name,
		IsValueDelta,
		StatValue,
		Statistic
	)
	SELECT
		@DeploymentId,
		@Id,
		@HostName,
		@Name,
		@IsValueDelta,
		@StatValue,
		@Statistic;
	COMMIT;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteReminderRowKey','
	DELETE FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND GrainId = @GrainId AND @GrainId IS NOT NULL
		AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL
		AND Version = @Version AND @Version IS NOT NULL;
	SELECT ROW_COUNT();
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteReminderRowsKey','
	DELETE FROM OrleansRemindersTable
	WHERE 
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL;
');
