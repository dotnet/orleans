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

5) ETag or VersionETag for Orleans is an opaque column that represents a unique version. The type of its actual implementation
   is not important as long as it represents a unique version.

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
-- This table defines Orleans operational queries. Orleans uses these to manage its operations,
-- these are the only queries Orleans issues to the database.
-- These can be redefined (e.g. to provide non-destructive updates) provided the stated interface principles hold.
CREATE TABLE OrleansQuery 
(
    QueryKey VARCHAR(64) NOT NULL,
    QueryText TEXT NOT NULL,


	CONSTRAINT OrleansQuery_Key PRIMARY KEY (QueryKey)
);


-- There will ever be only one (active) membership version table version column which will be updated periodically.
-- See table description at http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html. The following
-- IF-ELSE does SQL Server version detection and defines separate table structures and queries for them.
-- Orleans issues the queries as defined in [OrleansQuery] and operates through parameter names and types with no
-- regard to other matters.
	CREATE TABLE OrleansMembershipVersionTable 
	(
		DeploymentId NVARCHAR(150) NOT NULL,
		Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
		Version BIGINT NOT NULL,
		-- ETag should also always be unique, but there will ever be only one row.
		ETag BIGINT NOT NULL DEFAULT 0,
    
		CONSTRAINT PK_OrleansMembershipVersionTable_DeploymentId PRIMARY KEY (DeploymentId)
	);

	CREATE TABLE OrleansMembershipTable 
	(
		DeploymentId NVARCHAR(150) NOT NULL,
		Address VARCHAR(45) NOT NULL,
		Port INT NOT NULL,
		Generation INT NOT NULL,
		HostName NVARCHAR(150) NOT NULL,
		Status INT NOT NULL,
		ProxyPort INT NULL,
		RoleName NVARCHAR(150) NULL,
		InstanceName NVARCHAR(150) NULL,
    	UpdateZone INT NULL,
		FaultZone INT NULL,
		SuspectingSilos TEXT NULL,
		SuspectingTimes TEXT NULL,
		StartTime DATETIME NOT NULL,
		IAmAliveTime DATETIME NOT NULL,
		ETag BIGINT NOT NULL DEFAULT 0,
    
		-- A refactoring note: This combination needs to be unique, currently enforced by making it a primary key.
		-- See more information at http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html.
		CONSTRAINT PK_OrleansMembershipTable_DeploymentId PRIMARY KEY (DeploymentId , Address , Port , Generation),
		CONSTRAINT FK_MembershipTable_MembershipVersionTable_DeploymentId FOREIGN KEY (DeploymentId) REFERENCES OrleansMembershipVersionTable (DeploymentId)
	);

	CREATE TABLE OrleansRemindersTable 
	(
		ServiceId NVARCHAR(150) NOT NULL,
		GrainId NVARCHAR(150) NOT NULL,
		ReminderName NVARCHAR(150) NOT NULL,
		StartTime DATETIME NOT NULL,
		Period INT NOT NULL,
		GrainIdConsistentHash INT NOT NULL,
		ETag BIGINT NOT NULL DEFAULT 0,
    
		CONSTRAINT PK_OrleansRemindersTable_ServiceId_GrainId_ReminderName PRIMARY KEY (ServiceId , GrainId , ReminderName)
	);
	
	CREATE TABLE OrleansStatisticsTable 
	(
		OrleansStatisticsTableId INT NOT NULL AUTO_INCREMENT,
		DeploymentId NVARCHAR(150) NOT NULL,
		Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
		Id NVARCHAR(250) NOT NULL,
		HostName NVARCHAR(150) NOT NULL,
		Name NVARCHAR(150) NULL,
		IsDelta BIT NOT NULL,
		StatValue NVARCHAR(1024) NOT NULL,
		Statistic NVARCHAR(250) NOT NULL,
    
		CONSTRAINT OrleansStatisticsTable_OrleansStatisticsTableId PRIMARY KEY (OrleansStatisticsTableId)
	);

	CREATE TABLE OrleansClientMetricsTable 
	(
		DeploymentId NVARCHAR(150) NOT NULL,
		ClientId NVARCHAR(150) NOT NULL,
		Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
		Address VARCHAR(45) NOT NULL,
		HostName NVARCHAR(150) NOT NULL,
		CPU FLOAT NOT NULL,
		Memory BIGINT NOT NULL,
		SendQueue INT NOT NULL,
		ReceiveQueue INT NOT NULL,
		SentMessages BIGINT NOT NULL,
		ReceivedMessages BIGINT NOT NULL,
		ConnectedGatewayCount BIGINT NOT NULL,
    
		CONSTRAINT PK_OrleansClientMetricsTable_DeploymentId_ClientId PRIMARY KEY (DeploymentId , ClientId)
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
		GatewayAddress VARCHAR(45) NULL,
		GatewayPort INT NULL,
		CPU FLOAT NOT NULL,
		Memory BIGINT NOT NULL,
		Activations INT NOT NULL,
		RecentlyUsedActivations INT NOT NULL,
		SendQueue INT NOT NULL,
		ReceiveQueue INT NOT NULL,
		RequestQueue BIGINT NOT NULL,
		SentMessages BIGINT NOT NULL,
		ReceivedMessages BIGINT NOT NULL,
		LoadShedding BIT NOT NULL,
		ClientCount BIGINT NOT NULL,
    
		CONSTRAINT PK_OrleansSiloMetricsTable_DeploymentId_SiloId PRIMARY KEY (DeploymentId , SiloId),
		CONSTRAINT FK_SiloMetricsTable_MembershipVersionTable_DeploymentId FOREIGN KEY (DeploymentId) REFERENCES OrleansMembershipVersionTable (DeploymentId)
	);

	INSERT INTO OrleansQuery(QueryKey, QueryText)
	VALUES
	(
		'UpdateIAmAlivetimeKey',
		'
		-- This is not expected to never fail by Orleans, so return value
		-- is not needed nor is it checked.
	
		UPDATE OrleansMembershipTable 
		SET
			IAmAliveTime = @iAmAliveTime
		WHERE
			(DeploymentId = @deploymentId AND @deploymentId IS NOT NULL)
			AND (Address = @address AND @address IS NOT NULL)
			AND (Port = @port AND @port IS NOT NULL)
			AND (Generation = @generation AND @generation IS NOT NULL);
	');

	INSERT INTO OrleansQuery(QueryKey, QueryText)
	VALUES
	(
		'InsertMembershipVersionKey',
		' 
		INSERT INTO OrleansMembershipVersionTable
		(
			DeploymentId,
			Version
		)
		SELECT	
			@deploymentId,
			@version
		WHERE NOT EXISTS
		(			
			SELECT 1
			FROM OrleansMembershipVersionTable
			WHERE DeploymentId = @deploymentId AND @deploymentId IS NOT NULL
			FOR UPDATE
		);
                                        
		SELECT ROW_COUNT();
	');

DELIMITER ;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(		
	'InsertMembershipKey','call InsertMembershipKey(@deploymentId, @address, @port, @generation, @versionEtag, 
    @version, @hostName, @status, @proxyPort, @roleName, @instanceName, @updateZone, @faultZone, @suspectingSilos, 
    @suspectingTimes, @startTime, @iAmAliveTime);'
);

DELIMITER $$

CREATE PROCEDURE InsertMembershipKey(
	in	_deploymentId NVARCHAR(150),
	in	_address NVARCHAR(45),
	in	_port INT,
	in	_generation INT,
    in  _versionEtag BIGINT,
    in  _version BIGINT,
	in	_hostName NVARCHAR(150),
	in	_status INT,
	in	_proxyPort INT,
	in	_roleName NVARCHAR(150),
	in	_instanceName NVARCHAR(150),
	in	_updateZone INT,
	in	_faultZone INT,
	in	_suspectingSilos TEXT,
	in	_suspectingTimes TEXT,
	in	_startTime DATETIME,
	in	_iAmAliveTime DATETIME
)
BEGIN
	DECLARE _ROWCOUNT INT;
	START TRANSACTION;
		
		
		-- There is no need to check the condition for inserting
		-- as the necessary condition with regard to table membership
		-- protocol is enforced as part of the primary key definition.
		-- Inserting will fail if there is already a membership
		-- row with the same
		-- * DeploymentId 	= _deploymentId
		-- * Address		= _address
		-- * Port			= _port
		-- * Generation		= _generation
		--
		-- For more information on table membership protocol insert see at
		-- http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html and at
		-- https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs
		INSERT INTO OrleansMembershipTable
		(
			DeploymentId,
			Address,
			Port,
			Generation,
			HostName,
			Status,
			ProxyPort,
			RoleName,
			InstanceName,
			UpdateZone,
			FaultZone,
			SuspectingSilos,
			SuspectingTimes,
			StartTime,
			IAmAliveTime
		)
		VALUES
		(
			_deploymentId,
			_address,
			_port,
			_generation,
			_hostName,
			_status,
			_proxyPort,
			_roleName,
			_instanceName,
			_updateZone,
			_faultZone,
			_suspectingSilos,
			_suspectingTimes,
			_startTime,
			_iAmAliveTime
		);
		
		IF ROW_COUNT() = 1 
		THEN
			-- The transaction has not been rolled back. The following
			-- update must succeed or else the whole transaction needs
			-- to be rolled back.
			UPDATE OrleansMembershipVersionTable
			SET
				Version		= _version,
			 	Etag 		= _versionEtag + 1
			WHERE
				(DeploymentId	= _deploymentId AND _deploymentId IS NOT NULL)
				AND (ETag		= _versionEtag AND _versionEtag IS NOT NULL);
		END IF;
		-- Here the rowcount should always be either zero (no update)
		-- or one (exactly one entry updated).
        SET _ROWCOUNT = ROW_COUNT();
		IF _ROWCOUNT = 1
	 	THEN
			COMMIT;
	 	ELSE
	 		ROLLBACK;
	 	END IF;
        SELECT _ROWCOUNT;
	END$$

	DELIMITER ;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpdateMembershipKey','call UpdateMembershipKey(@deploymentId, @address, @port, @generation, @hostName,
	@status, @proxyPort, @roleName, @instanceName, @updateZone, @faultZone, @suspectingSilos, @suspectingTimes, 
    @startTime, @iAmAliveTime, @etag, @versionEtag, @version);'
);

DELIMITER $$

CREATE PROCEDURE UpdateMembershipKey(
	in	_deploymentId NVARCHAR(150),
	in	_address NVARCHAR(45),
	in	_port INT,
	in	_generation INT,
	in	_hostName NVARCHAR(150),
	in	_status INT,
	in	_proxyPort INT,
	in	_roleName NVARCHAR(150),
	in	_instanceName NVARCHAR(150),
	in	_updateZone INT,
	in	_faultZone INT,
	in	_suspectingSilos TEXT,
	in	_suspectingTimes TEXT,
	in	_startTime DATETIME,
	in	_iAmAliveTime DATETIME,
    in	_eTag BIGINT,
    in _versionEtag BIGINT,
    in _version BIGINT
)
BEGIN
		DECLARE _ROWCOUNT INT;
		START TRANSACTION;

		-- For more information on table membership protocol update see at
		-- http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html and at
		-- https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs.
		UPDATE OrleansMembershipTable
		SET
			Address				= _address,
			Port				= _port,
			Generation			= _generation,
			HostName			= _hostName,
			Status				= _status,
			ProxyPort			= _proxyPort,
			RoleName			= _roleName,
			InstanceName		= _instanceName,
			UpdateZone			= _updateZone,
			FaultZone			= _faultZone,
			SuspectingSilos		= _suspectingSilos,
			SuspectingTimes		= _suspectingTimes,
			StartTime			= _startTime,
			IAmAliveTime		= _iAmAliveTime,
			ETag				= _etag + 1
		WHERE
			(DeploymentId		= _deploymentId AND _deploymentId IS NOT NULL)
			AND (Address		= _address AND _address IS NOT NULL)
			AND (Port			= _port AND _port IS NOT NULL)
			AND (Generation		= _generation AND _generation IS NOT NULL)
			AND (ETag			= _etag and _etag IS NOT NULL);

		IF ROW_COUNT() = 1
		THEN
			-- The transaction has not been rolled back. The following
			-- update must succeed or else the whole transaction needs
			-- to be rolled back.
			UPDATE OrleansMembershipVersionTable
			SET
				Version		= _version,
				ETag		= _versionEtag + 1
			WHERE
				DeploymentId	= _deploymentId AND _deploymentId IS NOT NULL
				AND ETag		= _versionEtag AND _versionEtag IS NOT NULL;

		END IF;
        SET _ROWCOUNT = ROW_COUNT();
		IF _ROWCOUNT = 1
	 	THEN
			COMMIT;
	 	ELSE
	 		ROLLBACK;
	 	END IF;
        SELECT _ROWCOUNT;
END$$ 

DELIMITER ;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertReminderRowKey', 'call UpsertReminderRowKey(@serviceId, @grainId, @reminderName, @startTime, @period, 
    @grainIdConsistentHash);'
);

DELIMITER $$
CREATE PROCEDURE UpsertReminderRowKey(
	in 	_serviceId NVARCHAR(150),
	in 	_grainId NVARCHAR(150),
	in 	_reminderName NVARCHAR(150),
	in 	_startTime DATETIME,
	in 	_period INT,
	in 	_grainIdConsistentHash INT
)
BEGIN
		DECLARE _newEtag BIGINT;
		START TRANSACTION;
        SET _newEtag = (SELECT ETag + 1 FROM OrleansRemindersTable WHERE
						ServiceId = _serviceId AND _serviceId IS NOT NULL
						AND GrainId = _grainId AND _grainId IS NOT NULL
						AND ReminderName = _reminderName AND _reminderName IS NOT NULL 
						FOR UPDATE);
                        
		IF _newEtag IS NULL
		THEN
			SET _newEtag = 0;
			INSERT INTO OrleansRemindersTable
			(
				ServiceId,
				GrainId,
				ReminderName,
				StartTime,
				Period,
				GrainIdConsistentHash
			)
			VALUES
			(
				_serviceId,
				_grainId,
				_reminderName,
				_startTime,
				_period,
				_grainIdConsistentHash
			);
		ELSE
			UPDATE OrleansRemindersTable
			SET				
				StartTime             = _startTime,
				Period                = _period,
				GrainIdConsistentHash = _grainIdConsistentHash,
				ETag                  = _newEtag
			WHERE
				ServiceId = _serviceId AND _serviceId IS NOT NULL
				AND GrainId = _grainId AND _grainId IS NOT NULL
				AND ReminderName = _reminderName AND _reminderName IS NOT NULL;
		END IF;
		COMMIT;
		SELECT CONVERT(_newEtag, BINARY) AS ETag;
END$$

DELIMITER ;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertReportClientMetricsKey','call UpsertReportClientMetricsKey(@deploymentId, @clientId, @timestamp, 
    @address, @hostName, @cpuUsage, @memoryUsage, @sendQueueLength, @receiveQueueLength, @sentMessagesCount,
    @receivedMessagesCount, @connectedGatewaysCount);'
);

DELIMITER $$

CREATE PROCEDURE UpsertReportClientMetricsKey(
	in	_deploymentId NVARCHAR(150),
	in	_clientId NVARCHAR(150),
	in	_timestamp DATETIME,
	in	_address VARCHAR(45),
	in	_hostName NVARCHAR(150),
	in	_cpuUsage FLOAT,
	in	_memoryUsage BIGINT,
	in	_sendQueueLength INT,
	in	_receiveQueueLength INT,
	in	_sentMessagesCount BIGINT,
	in	_receivedMessagesCount BIGINT,
	in	_connectedGatewaysCount BIGINT
)
BEGIN
		START TRANSACTION;     
		IF EXISTS(SELECT 1 FROM OrleansClientMetricsTable WHERE
			DeploymentId = _deploymentId AND _deploymentId IS NOT NULL
			AND ClientId = _clientId AND _clientId IS NOT NULL
			FOR UPDATE) 
		THEN
			UPDATE OrleansClientMetricsTable
			SET			
				Address = _address,
				HostName = _hostName,
				CPU = _cpuUsage,
				Memory = _memoryUsage,
				SendQueue = _sendQueueLength,
				ReceiveQueue = _receiveQueueLength,
				SentMessages = _sentMessagesCount,
				ReceivedMessages = _receivedMessagesCount,
				ConnectedGatewayCount = _connectedGatewaysCount
			WHERE
				(DeploymentId = _deploymentId AND _deploymentId IS NOT NULL)
				AND (ClientId = _clientId AND _clientId IS NOT NULL);
		ELSE
			INSERT INTO OrleansClientMetricsTable
			(
				DeploymentId,
				ClientId,
				Address,			
				HostName,
				CPU,
				Memory,
				SendQueue,
				ReceiveQueue,
				SentMessages,
				ReceivedMessages,
				ConnectedGatewayCount
			)
			VALUES
			(
				_deploymentId,
				_clientId,
				_address,
				_hostName,
				_cpuUsage,
				_memoryUsage,
				_sendQueueLength,
				_receiveQueueLength,
				_sentMessagesCount,
				_receivedMessagesCount,
				_connectedGatewaysCount
			);
		END IF;
		COMMIT;
END$$    

DELIMITER ;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertSiloMetricsKey','call UpsertSiloMetricsKey(@deploymentId, @siloId, @address, @port, @generation, 
    @hostName, @gatewayAddress, @gatewayPort, @cpuUsage, @memoryUsage, @activationsCount, @recentlyUsedActivationsCount,
	@sendQueueLength, @receiveQueueLength, @requestQueueLength, @sentMessagesCount, @receivedMessagesCount,
	@isOverloaded, @clientCount);'
);

DELIMITER $$

CREATE PROCEDURE UpsertSiloMetricsKey(
	in	_deploymentId NVARCHAR(150),
	in	_siloId NVARCHAR(150),
    in	_timestamp DATETIME,
    in	_address VARCHAR(45),
    in	_port INT,
    in	_generation INT,
    in	_hostName NVARCHAR(150),
    in	_gatewayAddress VARCHAR(45),
    in	_gatewayPort INT,
    in	_cpuUsage FLOAT,
    in	_memoryUsage BIGINT,
    in	_activationsCount INT,
    in	_recentlyUsedActivationsCount INT,
    in	_sendQueueLength INT,
    in	_receiveQueueLength INT,
    in	_requestQueueLength BIGINT,
    in	_sentMessagesCount BIGINT,
	in	_receivedMessagesCount BIGINT,
    in	_isOverloaded BIT,
    in	_clientCount BIGINT
)
BEGIN
		START TRANSACTION;
		IF EXISTS(SELECT 1 FROM  OrleansSiloMetricsTable WHERE
			DeploymentId = _deploymentId AND _deploymentId IS NOT NULL
			AND SiloId = _siloId AND _siloId IS NOT NULL
			FOR UPDATE)
		THEN
			UPDATE OrleansSiloMetricsTable
			SET
				Address = _address,
				Port = _port,
				Generation = _generation,
				HostName = _hostName,
				GatewayAddress = _gatewayAddress,
				GatewayPort = _gatewayPort,
				CPU = _cpuUsage,
				Memory = _memoryUsage,
				Activations = _activationsCount,
				RecentlyUsedActivations = _recentlyUsedActivationsCount,
				SendQueue = _sendQueueLength,
				ReceiveQueue = _receiveQueueLength,
				RequestQueue = _requestQueueLength,
				SentMessages = _sentMessagesCount,
				ReceivedMessages = _receivedMessagesCount,
				LoadShedding = _isOverloaded,
				ClientCount = _clientCount
			WHERE
				(DeploymentId = _deploymentId AND _deploymentId IS NOT NULL)
				AND (SiloId = _siloId AND _siloId IS NOT NULL);
		ELSE
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
				CPU,
				Memory,
				Activations,
				RecentlyUsedActivations,
				SendQueue,
				ReceiveQueue,
				RequestQueue,
				SentMessages,	
				ReceivedMessages,
				LoadShedding,
				ClientCount
			)
			VALUES
			(
				_deploymentId,
				_siloId,
				_address,
				_port,
				_generation,
				_hostName,
				_gatewayAddress,
				_gatewayPort,
				_cpuUsage,
				_memoryUsage,
				_activationsCount,
				_recentlyUsedActivationsCount,
				_sendQueueLength,
				_receiveQueueLength,
				_requestQueueLength,
				_sentMessagesCount,
				_receivedMessagesCount,
				_isOverloaded,
				_clientCount
			);
		END IF;
		COMMIT;
END$$    

DELIMITER ;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ActiveGatewaysQueryKey',
	'
	SELECT
		Address,
		ProxyPort,
		Generation
	FROM
		OrleansMembershipTable
	WHERE
		DeploymentId = @deploymentId AND @deploymentId IS NOT NULL
		AND Status   = @status AND @status IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'MembershipReadRowKey',
	'
	SELECT
		v.DeploymentId,
		m.Address,
		m.Port,
		m.Generation,
		m.HostName,
		m.Status,
		m.ProxyPort,
		m.RoleName,
		m.InstanceName,
		m.UpdateZone,
		m.FaultZone,
		m.SuspectingSilos,
		m.SuspectingTimes,
		m.StartTime,
		m.IAmAliveTime,
		CONVERT(m.ETag, BINARY) AS ETag,
		v.Version,
		CONVERT(v.ETag, BINARY) AS VersionETag
	FROM
		OrleansMembershipVersionTable v
		-- This ensures the version table will returned even if there is no matching membership row.
		LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId	
		AND (Address = @address AND @address IS NOT NULL)
		AND (Port    = @port AND @port IS NOT NULL)
		AND (Generation = @generation AND @generation IS NOT NULL)
		WHERE v.DeploymentId = @deploymentId AND @deploymentId IS NOT NULL;
	
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'MembershipReadAllKey',
	'
	SELECT
		v.DeploymentId,
		m.Address,
		m.Port,
		m.Generation,
		m.HostName,
		m.Status,
		m.ProxyPort,
		m.RoleName,
		m.InstanceName,
		m.UpdateZone,
		m.FaultZone,
		m.SuspectingSilos,
		m.SuspectingTimes,
		m.StartTime,
		m.IAmAliveTime,
		CONVERT(m.ETag, BINARY) AS ETag,
		v.Version,
		CONVERT(v.ETag, BINARY) AS VersionETag
	FROM
		OrleansMembershipVersionTable v
		LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
	WHERE
		v.DeploymentId = @deploymentId AND @deploymentId IS NOT NULL;
'); 

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteMembershipTableEntriesKey',
	'
    START TRANSACTION;                                        
    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @deploymentId AND @deploymentId IS NOT NULL;

    DELETE FROM OrleansMembershipVersionTable
    WHERE DeploymentId = @deploymentId AND @deploymentId IS NOT NULL;
    COMMIT;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadReminderRowsKey',
	'
    SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		CONVERT(ETag, BINARY) AS ETag
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @serviceId AND @serviceId IS NOT NULL
		AND GrainId = @grainId AND @grainId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadReminderRowKey',
	'
    SELECT
	GrainId,
	ReminderName,
	StartTime,
	Period,
	CONVERT(ETag, BINARY) AS ETag
    FROM OrleansRemindersTable
    WHERE
	ServiceId = @serviceId AND @serviceId IS NOT NULL
	AND GrainId = @grainId AND @grainId IS NOT NULL
	AND ReminderName = @reminderName AND @reminderName IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadRangeRows1Key',
	'
		SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		CONVERT(ETag, BINARY) AS ETag
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @serviceId AND @serviceId IS NOT NULL
		AND (GrainIdConsistentHash > @beginHash AND @beginHash IS NOT NULL
				AND GrainIdConsistentHash <= @endHash AND @endHash IS NOT NULL);
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadRangeRows2Key',
	'
		SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		CONVERT(ETag, BINARY) AS ETag
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @serviceId AND @serviceId IS NOT NULL
		AND (GrainIdConsistentHash > @beginHash AND @beginHash IS NOT NULL
				OR GrainIdConsistentHash <= @endHash AND @endHash IS NOT NULL);
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertOrleansStatisticsKey',
	'
	  START TRANSACTION;
		INSERT INTO OrleansStatisticsTable
		(
			DeploymentId,
			Id,
			HostName,
			Name,
			IsDelta,
			StatValue,
			Statistic
		)
		SELECT
			@deploymentId,
			@id,
			@hostName,
			@name,
			@isDelta,
			@statValue,
			@statistic;
		COMMIT;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteReminderRowKey',
	'   
		DELETE FROM OrleansRemindersTable
		WHERE 
			ServiceId = @serviceId AND @serviceId IS NOT NULL
			AND GrainId = @grainId AND @grainId IS NOT NULL
			AND ReminderName = @reminderName AND @reminderName IS NOT NULL
			AND ETag = @etag AND @etag IS NOT NULL;
		SELECT ROW_COUNT();
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteReminderRowsKey',
	'
	  DELETE FROM OrleansRemindersTable
	  WHERE 
	      ServiceId = @serviceId AND @serviceId IS NOT NULL;
');
