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
CREATE TABLE "ORLEANSQUERY" 
(	
    "QUERYKEY" VARCHAR2(64 BYTE) NOT NULL ENABLE, 
    "QUERYTEXT" VARCHAR2(4000 BYTE), 
      
    CONSTRAINT "ORLEANSQUERY_PK" PRIMARY KEY ("QUERYKEY")
);
/

-- For each deployment, there will be only one (active) membership version table version column which will be updated periodically.
CREATE TABLE "ORLEANSMEMBERSHIPVERSIONTABLE" 
(	
    "DEPLOYMENTID" NVARCHAR2(150) NOT NULL ENABLE, 
    "TIMESTAMP" TIMESTAMP (6) DEFAULT sys_extract_utc(systimestamp) NOT NULL ENABLE, 
    "VERSION" NUMBER(*,0) DEFAULT 0, 
    
    CONSTRAINT "ORLEANSMEMBERSHIPVERSIONTA_PK" PRIMARY KEY ("DEPLOYMENTID")
);
/

-- Every silo instance has a row in the membership table.
CREATE TABLE "ORLEANSMEMBERSHIPTABLE" 
(	
    "DEPLOYMENTID" NVARCHAR2(150) NOT NULL ENABLE, 
    "ADDRESS" VARCHAR2(45 BYTE) NOT NULL ENABLE, 
    "PORT" NUMBER(*,0) NOT NULL ENABLE, 
    "GENERATION" NUMBER(*,0) NOT NULL ENABLE, 
    "SILONAME" NVARCHAR2(150) NOT NULL ENABLE, 
    "HOSTNAME" NVARCHAR2(150) NOT NULL ENABLE, 
    "STATUS" NUMBER(*,0) NOT NULL ENABLE, 
    "PROXYPORT" NUMBER(*,0), 
    "SUSPECTTIMES" VARCHAR2(4000 BYTE), 
    "STARTTIME" TIMESTAMP (6) NOT NULL ENABLE, 
    "IAMALIVETIME" TIMESTAMP (6) NOT NULL ENABLE, 
    
    CONSTRAINT "ORLEANSMEMBERSHIPTABLE_PK" PRIMARY KEY ("DEPLOYMENTID", "ADDRESS", "PORT", "GENERATION"),
    CONSTRAINT "ORLEANSMEMBERSHIPTABLE_FK1" FOREIGN KEY ("DEPLOYMENTID")
	  REFERENCES "ORLEANSMEMBERSHIPVERSIONTABLE" ("DEPLOYMENTID") ENABLE
);
/

-- Orleans Reminders table - http://dotnet.github.io/orleans/Advanced-Concepts/Timers-and-Reminders
CREATE TABLE "ORLEANSREMINDERSTABLE"
(
    "SERVICEID" NVARCHAR2(150) NOT NULL ENABLE,
    "GRAINID" VARCHAR2(150) NOT NULL,
    "REMINDERNAME" NVARCHAR2(150) NOT NULL,
    "STARTTIME" TIMESTAMP(6) NOT NULL ENABLE,
    "PERIOD" INT NULL,
    "GRAINHASH" INT NOT NULL,
    "VERSION" INT NOT NULL,
    
    CONSTRAINT PK_REMINDERSTABLE PRIMARY KEY(SERVICEID, GRAINID, REMINDERNAME)
);
/

CREATE TABLE "ORLEANSSTATISTICSTABLE" 
   (	
    "ORLEANSSTATISTICSTABLEID" NUMBER(*,0), 
	"DEPLOYMENTID" NVARCHAR2(150) NOT NULL ENABLE, 
	"TIMESTAMP" TIMESTAMP (6) DEFAULT sys_extract_utc(systimestamp) NOT NULL ENABLE, 
	"ID" NVARCHAR2(250) NOT NULL ENABLE, 
	"HOSTNAME" NVARCHAR2(150) NOT NULL ENABLE, 
	"NAME" NVARCHAR2(150) NOT NULL ENABLE, 
	"ISVALUEDELTA" NUMBER(*,0) NOT NULL ENABLE, 
	"STATVALUE" NVARCHAR2(1024), 
	"STATISTIC" NVARCHAR2(512) NOT NULL ENABLE, 
	 CONSTRAINT "ORLEANSSTATISTICSTABLE_PK" PRIMARY KEY ("ORLEANSSTATISTICSTABLEID")
   );
/

CREATE SEQUENCE "ORLEANSSTATISTICSTABLE_SEQ"  MINVALUE 1 MAXVALUE 9999999999999999999999999999 INCREMENT BY 1 START WITH 1 CACHE 20 NOORDER  NOCYCLE;

CREATE OR REPLACE TRIGGER "ORLEANSSTATISTICSTABLE_TRG" 
BEFORE INSERT ON ORLEANSSTATISTICSTABLE 
FOR EACH ROW 
BEGIN
  <<COLUMN_SEQUENCES>>
  BEGIN
    IF INSERTING AND :NEW.ORLEANSSTATISTICSTABLEID IS NULL THEN
      SELECT ORLEANSSTATISTICSTABLE_SEQ.NEXTVAL INTO :NEW.ORLEANSSTATISTICSTABLEID FROM SYS.DUAL;
    END IF;
  END COLUMN_SEQUENCES;
END;
/
ALTER TRIGGER "ORLEANSSTATISTICSTABLE_TRG" ENABLE;


CREATE TABLE "ORLEANSCLIENTMETRICSTABLE" 
(	
    "DEPLOYMENTID" VARCHAR2(150 BYTE) NOT NULL ENABLE, 
    "CLIENTID" VARCHAR2(150 BYTE) NOT NULL ENABLE, 
    "TIMESTAMP" TIMESTAMP (6) DEFAULT sys_extract_utc(systimestamp) NOT NULL ENABLE, 
    "ADDRESS" VARCHAR2(45 BYTE) NOT NULL ENABLE, 
    "HOSTNAME" NVARCHAR2(150) NOT NULL ENABLE, 
    "CPUUSAGE" FLOAT(126) NOT NULL ENABLE, 
    "MEMORYUSAGE" NUMBER(19,0) NOT NULL ENABLE, 
    "SENDQUEUELENGTH" NUMBER(*,0) NOT NULL ENABLE, 
    "RECEIVEQUEUELENGTH" NUMBER(*,0) NOT NULL ENABLE, 
    "SENTMESSAGES" NUMBER(19,0) NOT NULL ENABLE, 
    "RECEIVEDMESSAGES" NUMBER(19,0) NOT NULL ENABLE, 
    "CONNECTEDGATEWAYCOUNT" NUMBER(19,0) NOT NULL ENABLE,
    
	 CONSTRAINT "ORLEANSCLIENTMETRICSTABLE_PK" PRIMARY KEY ("DEPLOYMENTID", "CLIENTID")
);
/
 CREATE TABLE "ORLEANSSILOMETRICSTABLE" 
(	
    "DEPLOYMENTID" NVARCHAR2(150) NOT NULL ENABLE, 
    "SILOID" NVARCHAR2(150) NOT NULL ENABLE, 
    "TIMESTAMP" TIMESTAMP (6) DEFAULT sys_extract_utc(systimestamp) NOT NULL ENABLE, 
    "ADDRESS" VARCHAR2(45 BYTE) NOT NULL ENABLE, 
    "PORT" NUMBER(*,0) NOT NULL ENABLE, 
    "GENERATION" NUMBER(*,0) NOT NULL ENABLE, 
    "HOSTNAME" NVARCHAR2(150) NOT NULL ENABLE, 
    "GATEWAYADDRESS" VARCHAR2(45 BYTE) NOT NULL ENABLE, 
    "GATEWAYPORT" NUMBER(*,0) NOT NULL ENABLE, 
    "CPUUSAGE" FLOAT(126) NOT NULL ENABLE, 
    "MEMORYUSAGE" NUMBER(19,0) NOT NULL ENABLE, 
    "SENDQUEUELENGTH" NUMBER(*,0) NOT NULL ENABLE, 
    "RECEIVEQUEUELENGTH" NUMBER(*,0) NOT NULL ENABLE, 
    "SENTMESSAGES" NUMBER(19,0) NOT NULL ENABLE, 
    "RECEIVEDMESSAGES" NUMBER(19,0) NOT NULL ENABLE, 
    "ACTIVATIONCOUNT" NUMBER(*,0) NOT NULL ENABLE, 
    "RECENTLYUSEDACTIVATIONCOUNT" NUMBER(*,0) NOT NULL ENABLE, 
    "REQUESTQUEUELENGTH" NUMBER(19,0) NOT NULL ENABLE, 
    "ISOVERLOADED" NUMBER(*,0) NOT NULL ENABLE, 
    "CLIENTCOUNT" NUMBER(19,0) NOT NULL ENABLE, 
    
    CONSTRAINT "ORLEANSSILOMETRICSTABLE_PK" PRIMARY KEY ("DEPLOYMENTID", "SILOID"), 
    CONSTRAINT "ORLEANSSILOMETRICSTABLE_FK1" FOREIGN KEY ("DEPLOYMENTID") 
	  REFERENCES "ORLEANSMEMBERSHIPVERSIONTABLE" ("DEPLOYMENTID") ON DELETE CASCADE ENABLE
);
/


-- The design criteria for this table are:
--
-- 1. It can contain arbitrary content serialized as binary, XML or JSON. These formats
-- are supported to allow one to take advantage of in-storage processing capabilities for
-- these types if required. This should not incur extra cost on storage.
--
-- 2. The table design should scale with the idea of tens or hundreds (or even more) types
-- of grains that may operate with even hundreds of thousands of grain IDs within each
-- type of a grain.
--
-- 3. The table and its associated operations should remain stable. There should not be
-- structural reason for unexpected delays in operations. It should be possible to also
-- insert data reasonably fast without resource contention.
--
-- 4. For reasons in 2. and 3., the index should be as narrow as possible so it fits well in
-- memory and should it require maintenance, isn't resource intensive. For this
-- reason the index is narrow by design (ideally non-clustered). Currently the entity
-- is recognized in the storage by the grain type and its ID, which are unique in Orleans silo.
-- The ID is the grain ID bytes (if string type UTF-8 bytes) and possible extension key as UTF-8
-- bytes concatenated with the ID and then hashed.
--
-- Reason for hashing: Database engines usually limit the length of the column sizes, which
-- would artificially limit the length of IDs or types. Even when within limitations, the
-- index would be thick and consume more memory.
--
-- In the current setup the ID and the type are hashed into two INT type instances, which
-- are made a compound index. When there are no collisions, the index can quickly locate
-- the unique row. Along with the hashed index values, the NVARCHAR(nnn) values are also
-- stored and they are used to prune hash collisions down to only one result row.
--
-- 5. The design leads to duplication in the storage. It is reasonable to assume there will
-- a low number of services with a given service ID operational at any given time. Or that
-- compared to the number of grain IDs, there are a fairly low number of different types of
-- grain. The catch is that were these data separated to another table, it would make INSERT
-- and UPDATE operations complicated and would require joins, temporary variables and additional
-- indexes or some combinations of them to make it work. It looks like fitting strategy
-- could be to use table compression.
--
-- 6. For the aforementioned reasons, grain state DELETE will set NULL to the data fields
-- and updates the Version number normally. This should alleviate the need for index or
-- statistics maintenance with the loss of some bytes of storage space. The table can be scrubbed
-- in a separate maintenance operation.
--
-- 7. In the storage operations queries the columns need to be in the exact same order
-- since the storage table operations support optionally streaming.
CREATE TABLE "STORAGE" 
(	

    -- These are for the book keeping. Orleans calculates
    -- these hashes (see RelationalStorageProvide implementation),
	-- which are signed 32 bit integers mapped to the *Hash fields.
	-- The mapping is done in the code. The
    -- *String columns contain the corresponding clear name fields.
	--
	-- If there are duplicates, they are resolved by using GrainIdN0,
	-- GrainIdN1, GrainIdExtensionString and GrainTypeString fields.
	-- It is assumed these would be rarely needed.
    "GRAINIDHASH" NUMBER(*,0) NOT NULL ENABLE, 
	"GRAINIDN0" NUMBER(19,0) NOT NULL ENABLE, 
	"GRAINIDN1" NUMBER(19,0) NOT NULL ENABLE, 
	"GRAINTYPEHASH" NUMBER(*,0) NOT NULL ENABLE, 
	"GRAINTYPESTRING" NVARCHAR2(512) NOT NULL ENABLE, 
	"GRAINIDEXTENSIONSTRING" NVARCHAR2(512), 
	"SERVICEID" NVARCHAR2(150) NOT NULL ENABLE, 
    
    
    -- The usage of the Payload records is exclusive in that
    -- only one should be populated at any given time and two others
    -- are NULL. The types are separated to advantage on special
	-- processing capabilities present on database engines (not all might
	-- have both JSON and XML types.
	--
	-- One is free to alter the size of these fields.
	"PAYLOADBINARY" BLOB, 
	"PAYLOADXML" CLOB, 
	"PAYLOADJSON" CLOB, 
    -- Informational field, no other use.
	"MODIFIEDON" TIMESTAMP (6) NOT NULL ENABLE, 
    -- The version of the stored payload.
	"VERSION" NUMBER(*,0)
    
    -- The following would in principle be the primary key, but it would be too thick
	-- to be indexed, so the values are hashed and only collisions will be solved
	-- by using the fields. That is, after the indexed queries have pinpointed the right
	-- rows down to [0, n] relevant ones, n being the number of collided value pairs.
);
CREATE INDEX "IX_STORAGE" ON "STORAGE" ("GRAINIDHASH", "GRAINTYPEHASH") PARALLEL 
COMPRESS;
/
-- Oracle specific implementation note:
-- Some OrleansQueries are implemented as functions and differ from the scripts of other databases. 
-- The main reason for this is the fact, that oracle doesn´t support returning variables from queries
-- directly. So in the case that a variable value is needed as output of a OrleansQuery (e.g. version)
-- a function is used.

CREATE OR REPLACE FUNCTION InsertMembership(PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_IAMALIVETIME IN TIMESTAMP, PARAM_SILONAME IN NVARCHAR2, PARAM_HOSTNAME IN NVARCHAR2, PARAM_ADDRESS IN VARCHAR2,
                                    PARAM_PORT IN NUMBER, PARAM_GENERATION IN NUMBER, PARAM_STARTTIME IN TIMESTAMP, PARAM_STATUS IN NUMBER, PARAM_PROXYPORT IN NUMBER, PARAM_VERSION IN NUMBER)
  RETURN NUMBER IS
  rowcount NUMBER;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
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
      PARAM_DEPLOYMENTID,
      PARAM_ADDRESS,
      PARAM_PORT,
      PARAM_GENERATION,
      PARAM_SILONAME,
      PARAM_HOSTNAME,
      PARAM_STATUS,
      PARAM_PROXYPORT,
      PARAM_STARTTIME,
      PARAM_IAMALIVETIME
    FROM DUAL WHERE NOT EXISTS
    (
      SELECT 1 FROM OrleansMembershipTable WHERE
        DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
        AND Address = PARAM_ADDRESS AND PARAM_ADDRESS IS NOT NULL
        AND Port = PARAM_PORT AND PARAM_PORT IS NOT NULL
        AND Generation = PARAM_GENERATION AND PARAM_GENERATION IS NOT NULL
    );
    rowcount :=	SQL%ROWCOUNT;
    UPDATE OrleansMembershipVersionTable
    SET Timestamp = sys_extract_utc(systimestamp),
        Version = Version + 1
    WHERE
  		DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
    	AND Version = PARAM_VERSION AND PARAM_VERSION IS NOT NULL
      AND rowcount > 0;
    rowcount :=	SQL%ROWCOUNT;
    IF rowcount = 0 THEN
      ROLLBACK;
    ELSE
      COMMIT;
    END IF;
  
    IF rowcount > 0 THEN
      RETURN(1);
    ELSE
      RETURN(0);
    END IF;
  END;
/

CREATE OR REPLACE FUNCTION UpdateMembership(PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_ADDRESS IN VARCHAR2, PARAM_PORT IN NUMBER, PARAM_GENERATION IN NUMBER,
                                               PARAM_IAMALIVETIME IN TIMESTAMP, PARAM_STATUS IN NUMBER, PARAM_SUSPECTTIMES IN VARCHAR2, PARAM_VERSION IN NUMBER
                                              )
  RETURN NUMBER IS
  rowcount NUMBER;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    UPDATE OrleansMembershipVersionTable
      SET
        Timestamp = sys_extract_utc(systimestamp),
        Version = Version + 1
    WHERE
		DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
		AND Version = PARAM_VERSION AND PARAM_VERSION IS NOT NULL;
    rowcount := SQL%ROWCOUNT;
    UPDATE OrleansMembershipTable
      SET
        Status = PARAM_STATUS,
        SuspectTimes = PARAM_SUSPECTTIMES,
        IAmAliveTime = PARAM_IAMALIVETIME
      WHERE DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
        AND Address = PARAM_ADDRESS AND PARAM_ADDRESS IS NOT NULL
        AND Port = PARAM_PORT AND PARAM_PORT IS NOT NULL
        AND Generation = PARAM_GENERATION AND PARAM_GENERATION IS NOT NULL
        AND rowcount > 0;
    rowcount := SQL%ROWCOUNT;
    COMMIT;
    RETURN(rowcount);
  END;
/

CREATE OR REPLACE FUNCTION UpsertReminderRow(PARAM_SERVICEID IN NVARCHAR2, PARAM_GRAINHASH IN INT, PARAM_GRAINID IN VARCHAR2, PARAM_REMINDERNAME IN NVARCHAR2,
                                                PARAM_STARTTIME IN TIMESTAMP, PARAM_PERIOD IN NUMBER)
RETURN NUMBER IS
  rowcount NUMBER;
  currentVersion NUMBER := 0;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN     
    MERGE INTO OrleansRemindersTable ort
    USING (
      SELECT PARAM_SERVICEID as SERVICEID,
        PARAM_GRAINID as GRAINID,
        PARAM_REMINDERNAME as REMINDERNAME,
        PARAM_STARTTIME as STARTTIME,
        PARAM_PERIOD as PERIOD,
        PARAM_GRAINHASH GRAINHASH 
      FROM dual
    ) n_ort
    ON (ort.ServiceId = n_ort.SERVICEID AND
        ort.GrainId = n_ort.GRAINID AND
        ort.ReminderName = n_ort.REMINDERNAME
    )
    WHEN MATCHED THEN
    UPDATE SET
      ort.StartTime = n_ort.STARTTIME,
      ort.Period = n_ort.PERIOD,
      ort.GrainHash = n_ort.GRAINHASH,
      ort.Version = ort.Version+1
    WHEN NOT MATCHED THEN
    INSERT (ort.ServiceId, ort.GrainId, ort.ReminderName, ort.StartTime, ort.Period, ort.GrainHash, ort.Version)
    VALUES (n_ort.SERVICEID, n_ort.GRAINID, n_ort.REMINDERNAME, n_ort.STARTTIME, n_ort.PERIOD, n_ort.GRAINHASH, 0);
       
    SELECT Version INTO currentVersion FROM OrleansRemindersTable  
        WHERE ServiceId = PARAM_SERVICEID AND PARAM_SERVICEID IS NOT NULL
        AND GrainId = PARAM_GRAINID AND PARAM_GRAINID IS NOT NULL
        AND ReminderName = PARAM_REMINDERNAME AND PARAM_REMINDERNAME IS NOT NULL;
    COMMIT;
    RETURN(currentVersion);
  END;
/

CREATE OR REPLACE FUNCTION UpsertReportClientMetrics(PARAM_DEPLOYMENTID IN VARCHAR2, PARAM_HOSTNAME IN NVARCHAR2, PARAM_CPUUSAGE IN FLOAT, PARAM_MEMORYUSAGE IN NUMBER,
                                                        PARAM_SENDQUEUELENGTH IN NUMBER, PARAM_RECEIVEQUEUELENGTH IN NUMBER, PARAM_SENTMESSAGES IN NUMBER,
                                                        PARAM_RECEIVEDMESSAGES IN NUMBER, PARAM_CONNECTEDGATEWAYCOUNT IN NUMBER, PARAM_CLIENTID IN VARCHAR2,
                                                        PARAM_ADDRESS IN VARCHAR2)
RETURN NUMBER IS
  rowcount NUMBER;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    UPDATE OrleansClientMetricsTable
      SET
        Timestamp = sys_extract_utc(systimestamp),
        Address = PARAM_ADDRESS,
        HostName = PARAM_HOSTNAME,
        CpuUsage = PARAM_CPUUSAGE,
        MemoryUsage = PARAM_MEMORYUSAGE,
        SendQueueLength = PARAM_SENDQUEUELENGTH,
        ReceiveQueueLength = PARAM_RECEIVEQUEUELENGTH,
        SentMessages = PARAM_SENTMESSAGES,
        ReceivedMessages = PARAM_RECEIVEDMESSAGES,
        ConnectedGatewayCount = PARAM_CONNECTEDGATEWAYCOUNT
      WHERE DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
        AND ClientId = PARAM_CLIENTID AND PARAM_CLIENTID IS NOT NULL;
      
      rowcount := SQL%ROWCOUNT;
      
      IF rowcount = 1 THEN
        COMMIT;
        RETURN(1);
      END IF;
      
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
    SELECT
      PARAM_DEPLOYMENTID,
      PARAM_CLIENTID,
      PARAM_ADDRESS,
      PARAM_HOSTNAME,
      PARAM_CPUUSAGE,
      PARAM_MEMORYUSAGE,
      PARAM_SENDQUEUELENGTH,
      PARAM_RECEIVEQUEUELENGTH,
      PARAM_SENTMESSAGES,
      PARAM_RECEIVEDMESSAGES,
      PARAM_CONNECTEDGATEWAYCOUNT
    FROM DUAL;
    
    COMMIT;
    RETURN(1);
  END;
/

CREATE OR REPLACE FUNCTION UpsertSiloMetrics(PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_HOSTNAME IN NVARCHAR2, PARAM_CPUUSAGE IN FLOAT, PARAM_MEMORYUSAGE IN NUMBER, PARAM_SENDQUEUELENGTH IN NUMBER,
                                                PARAM_RECEIVEQUEUELENGTH IN NUMBER, PARAM_SENTMESSAGES IN NUMBER, PARAM_RECEIVEDMESSAGES IN NUMBER, PARAM_ACTIVATIONCOUNT IN NUMBER, PARAM_RECENTLYUSEDACTIVATIONS IN NUMBER,
                                                PARAM_REQUESTQUEUELENGHT IN NUMBER, PARAM_ISOVERLOADED IN NUMBER, PARAM_CLIENTCOUNT IN NUMBER, PARAM_ADDRESS IN VARCHAR2,
                                                PARAM_PORT IN NUMBER, PARAM_GENERATION IN NUMBER, PARAM_GATEWAYADDRESS IN VARCHAR2, PARAM_GATEWAYPORT IN NUMBER, PARAM_SILOID IN NVARCHAR2)
RETURN NUMBER IS
  rowcount NUMBER;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    UPDATE OrleansSiloMetricsTable
    SET
      Timestamp = sys_extract_utc(systimestamp),
      Address = PARAM_ADDRESS,
      Port = PARAM_PORT,
      Generation = PARAM_GENERATION,
      HostName = PARAM_HOSTNAME,
      GatewayAddress = PARAM_GATEWAYADDRESS,
      GatewayPort = PARAM_GATEWAYPORT,
      CpuUsage = PARAM_CPUUSAGE,
      MemoryUsage = PARAM_MEMORYUSAGE,
      ActivationCount = PARAM_ACTIVATIONCOUNT,
      RecentlyUsedActivationCount = PARAM_RECENTLYUSEDACTIVATIONS,
      SendQueueLength = PARAM_SENDQUEUELENGTH,
      ReceiveQueueLength = PARAM_RECEIVEQUEUELENGTH,
      RequestQueueLength = PARAM_REQUESTQUEUELENGHT,
      SentMessages = PARAM_SENTMESSAGES,
      ReceivedMessages = PARAM_RECEIVEDMESSAGES,
      IsOverloaded = PARAM_ISOVERLOADED,
      ClientCount = PARAM_CLIENTCOUNT
    WHERE
      DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
        AND SiloId = PARAM_SILOID AND PARAM_SILOID IS NOT NULL;
      
      rowcount := SQL%ROWCOUNT;
      
      IF rowcount = 1 THEN
        COMMIT;
        RETURN(1);
      END IF;
      
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
      SELECT
        PARAM_DEPLOYMENTID,
        PARAM_SILOID,
        PARAM_ADDRESS,
        PARAM_PORT,
        PARAM_GENERATION,
        PARAM_HOSTNAME,
        PARAM_GATEWAYADDRESS,
        PARAM_GATEWAYPORT,
        PARAM_CPUUSAGE,
        PARAM_MEMORYUSAGE,
        PARAM_SENDQUEUELENGTH,
        PARAM_RECEIVEQUEUELENGTH,
        PARAM_SENTMESSAGES,
        PARAM_RECEIVEDMESSAGES,
        PARAM_ACTIVATIONCOUNT,
        PARAM_RECENTLYUSEDACTIVATIONS,
        PARAM_REQUESTQUEUELENGHT,
        PARAM_ISOVERLOADED,
        PARAM_CLIENTCOUNT
      FROM DUAL;
    
    COMMIT;
    RETURN(1);
  END;
/

CREATE OR REPLACE FUNCTION DeleteReminderRow(PARAM_SERVICEID IN NVARCHAR2, PARAM_GRAINID IN VARCHAR2, PARAM_REMINDERNAME IN NVARCHAR2,
                                                PARAM_VERSION IN NUMBER)
RETURN NUMBER IS
  rowcount NUMBER;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN     
    DELETE FROM OrleansRemindersTable 
      WHERE ServiceId = PARAM_SERVICEID AND PARAM_SERVICEID IS NOT NULL
        AND GrainId = PARAM_GRAINID AND PARAM_GRAINID IS NOT NULL
        AND ReminderName = PARAM_REMINDERNAME AND PARAM_REMINDERNAME IS NOT NULL
        AND Version = PARAM_VERSION AND PARAM_VERSION IS NOT NULL;
	
    rowcount := SQL%ROWCOUNT;

    COMMIT;
    RETURN(rowcount);
  END;
/

CREATE OR REPLACE FUNCTION WriteToStorage(PARAM_GRAINIDHASH IN NUMBER, PARAM_GRAINIDN0 IN NUMBER, PARAM_GRAINIDN1 IN NUMBER, PARAM_GRAINTYPEHASH IN NUMBER, PARAM_GRAINTYPESTRING IN NVARCHAR2,
                                             PARAM_GRAINIDEXTENSIONSTRING IN NVARCHAR2, PARAM_SERVICEID IN VARCHAR2, PARAM_GRAINSTATEVERSION IN NUMBER, PARAM_PAYLOADBINARY IN BLOB,
                                             PARAM_PAYLOADJSON IN CLOB, PARAM_PAYLOADXML IN CLOB)
  RETURN NUMBER IS
  rowcount NUMBER;
  newGrainStateVersion NUMBER := PARAM_GRAINSTATEVERSION;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    -- When Orleans is running in normal, non-split state, there will
	-- be only one grain with the given ID and type combination only. This
	-- grain saves states mostly serially if Orleans guarantees are upheld. Even
	-- if not, the updates should work correctly due to version number.
	--
	-- In split brain situations there can be a situation where there are two or more
	-- grains with the given ID and type combination. When they try to INSERT
	-- concurrently, the table needs to be locked pessimistically before one of
	-- the grains gets @GrainStateVersion = 1 in return and the other grains will fail
	-- to update storage. The following arrangement is made to reduce locking in normal operation.
	--
	-- If the version number explicitly returned is still the same, Orleans interprets it so the update did not succeed
	-- and throws an InconsistentStateException.
	--
	-- See further information at http://dotnet.github.io/orleans/Getting-Started-With-Orleans/Grain-Persistence.
  
  
	-- If the @GrainStateVersion is not zero, this branch assumes it exists in this database.
	-- The NULL value is supplied by Orleans when the state is new.
	IF newGrainStateVersion IS NOT NULL THEN
		UPDATE Storage
		SET
			PayloadBinary = PARAM_PAYLOADBINARY,
			PayloadJson = PARAM_PAYLOADJSON,
			PayloadXml = PARAM_PAYLOADXML,
			ModifiedOn = sys_extract_utc(systimestamp),
			Version = Version + 1
		WHERE
			GrainIdHash = PARAM_GRAINIDHASH AND PARAM_GRAINIDHASH IS NOT NULL
			AND GrainTypeHash = PARAM_GRAINTYPEHASH AND PARAM_GRAINTYPEHASH IS NOT NULL
			AND (GrainIdN0 = PARAM_GRAINIDN0 OR PARAM_GRAINIDN0 IS NULL)
			AND (GrainIdN1 = PARAM_GRAINIDN1 OR PARAM_GRAINIDN1 IS NULL)
			AND (GrainTypeString = PARAM_GRAINTYPESTRING OR PARAM_GRAINTYPESTRING IS NULL)
			AND ((PARAM_GRAINIDEXTENSIONSTRING IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = PARAM_GRAINIDEXTENSIONSTRING) OR PARAM_GRAINIDEXTENSIONSTRING IS NULL AND GrainIdExtensionString IS NULL)
			AND ServiceId = PARAM_SERVICEID AND PARAM_SERVICEID IS NOT NULL
			AND Version IS NOT NULL AND Version = PARAM_GRAINSTATEVERSION AND PARAM_GRAINSTATEVERSION IS NOT NULL
    RETURNING Version INTO newGrainStateVersion;
    
    rowcount := SQL%ROWCOUNT;
    
    IF rowcount = 1 THEN
      COMMIT;
      RETURN(newGrainStateVersion);
    END IF;
	END IF;
    
	-- The grain state has not been read. The following locks rather pessimistically
	-- to ensure only one INSERT succeeds.
	IF PARAM_GRAINSTATEVERSION IS NULL THEN
		INSERT INTO Storage
		(
			GrainIdHash,
			GrainIdN0,
			GrainIdN1,
			GrainTypeHash,
			GrainTypeString,
			GrainIdExtensionString,
			ServiceId,
			PayloadBinary,
			PayloadJson,
			PayloadXml,
			ModifiedOn,
			Version
		)
		SELECT
			PARAM_GRAINIDHASH,
			PARAM_GRAINIDN0,
			PARAM_GRAINIDN1,
			PARAM_GRAINTYPEHASH,
			PARAM_GRAINTYPESTRING,
			PARAM_GRAINIDEXTENSIONSTRING,
			PARAM_SERVICEID,
			PARAM_PAYLOADBINARY,
			PARAM_PAYLOADJSON,
			PARAM_PAYLOADXML,
			sys_extract_utc(systimestamp),
			1 FROM DUAL
		 WHERE NOT EXISTS
		 (
			-- There should not be any version of this grain state.
			SELECT 1
			FROM Storage
			WHERE
				GrainIdHash = PARAM_GRAINIDHASH AND PARAM_GRAINIDHASH IS NOT NULL
				AND GrainTypeHash = PARAM_GRAINTYPEHASH AND PARAM_GRAINTYPEHASH IS NOT NULL
				AND (GrainIdN0 = PARAM_GRAINIDN0 OR PARAM_GRAINIDN0 IS NULL)
				AND (GrainIdN1 = PARAM_GRAINIDN1 OR PARAM_GRAINIDN1 IS NULL)
				AND (GrainTypeString = PARAM_GRAINTYPESTRING OR PARAM_GRAINTYPESTRING IS NULL)
				AND ((PARAM_GRAINIDEXTENSIONSTRING IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = PARAM_GRAINIDEXTENSIONSTRING) OR PARAM_GRAINIDEXTENSIONSTRING IS NULL AND GrainIdExtensionString IS NULL)
				AND ServiceId = PARAM_SERVICEID AND PARAM_SERVICEID IS NOT NULL
		 );
     
     rowCount := SQL%ROWCOUNT;
     
		IF rowCount > 0 THEN
			newGrainStateVersion := 1;
		END IF;
	END IF;
  COMMIT;
	RETURN(newGrainStateVersion);
  END;
/

CREATE OR REPLACE FUNCTION ClearStorage(PARAM_GRAINIDHASH IN NUMBER, PARAM_GRAINIDN0 IN NUMBER, PARAM_GRAINIDN1 IN NUMBER, PARAM_GRAINTYPEHASH IN NUMBER, PARAM_GRAINTYPESTRING IN NVARCHAR2,
                                             PARAM_GRAINIDEXTENSIONSTRING IN NVARCHAR2, PARAM_SERVICEID IN VARCHAR2, PARAM_GRAINSTATEVERSION IN NUMBER)
  RETURN NUMBER IS
  rowcount NUMBER;
  newGrainStateVersion NUMBER := PARAM_GRAINSTATEVERSION;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    UPDATE Storage
    SET
	    PayloadBinary = NULL,
	    PayloadJson = NULL,
	    PayloadXml = NULL,
	    ModifiedOn = sys_extract_utc(systimestamp),
	    Version = Version + 1
    WHERE GrainIdHash = PARAM_GRAINIDHASH AND PARAM_GRAINIDHASH IS NOT NULL
      AND GrainTypeHash = PARAM_GRAINTYPEHASH AND PARAM_GRAINTYPEHASH IS NOT NULL
      AND (GrainIdN0 = PARAM_GRAINIDN0 OR PARAM_GRAINIDN0 IS NULL)
      AND (GrainIdN1  = PARAM_GRAINIDN1 OR PARAM_GRAINIDN1 IS NULL)
      AND (GrainTypeString = PARAM_GRAINTYPESTRING OR PARAM_GRAINTYPESTRING IS NULL)
      AND ((PARAM_GRAINIDEXTENSIONSTRING IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = PARAM_GRAINIDEXTENSIONSTRING) OR PARAM_GRAINIDEXTENSIONSTRING IS NULL AND GrainIdExtensionString IS NULL)
      AND ServiceId = PARAM_SERVICEID AND PARAM_SERVICEID IS NOT NULL
      AND Version IS NOT NULL AND Version = PARAM_GRAINSTATEVERSION AND PARAM_GRAINSTATEVERSION IS NOT NULL
    RETURNING Version INTO newGrainStateVersion;
    
    COMMIT;
    RETURN(newGrainStateVersion);
  END;
/

CREATE OR REPLACE FUNCTION InsertMembershipVersion(PARAM_DEPLOYMENTID IN NVARCHAR2)
RETURN NUMBER IS
rowcount NUMBER;
PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
  INSERT INTO OrleansMembershipVersionTable
      (
        DeploymentId
      )
      SELECT PARAM_DEPLOYMENTID FROM DUAL WHERE NOT EXISTS
      (
        SELECT 1 FROM OrleansMembershipVersionTable WHERE
        DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
      );
      rowCount := SQL%ROWCOUNT;
      
      COMMIT;
      RETURN(rowCount);
END;
/

CREATE OR REPLACE FUNCTION UpdateIAmAlivetime(PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_ADDRESS in VARCHAR2, PARAM_PORT IN NUMBER, 
                                                 PARAM_GENERATION IN NUMBER, PARAM_IAMALIVE IN TIMESTAMP)
RETURN NUMBER IS
rowcount NUMBER;
PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
    UPDATE OrleansMembershipTable
        SET
            IAmAliveTime = PARAM_IAMALIVE
        WHERE
            DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
            AND Address = PARAM_ADDRESS AND PARAM_ADDRESS IS NOT NULL
            AND Port = PARAM_PORT AND PARAM_PORT IS NOT NULL
            AND Generation = PARAM_GENERATION AND PARAM_GENERATION IS NOT NULL;
      COMMIT;
      RETURN(0);
END;
/


INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpdateIAmAlivetimeKey','
	SELECT UpdateIAmAlivetime(:DEPLOYMENTID, :ADDRESS, :PORT, :GENERATION, :IAMALIVETIME) AS RESULT FROM DUAL
');
/
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertMembershipVersionKey','
	SELECT InsertMembershipVersion(:DEPLOYMENTID) AS RESULT FROM DUAL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertMembershipKey','
	SELECT INSERTMEMBERSHIP(:DEPLOYMENTID,:IAMALIVETIME,:SILONAME,:HOSTNAME,:ADDRESS,:PORT,:GENERATION,:STARTTIME,:STATUS,:PROXYPORT,:VERSION) FROM DUAL
');
/ 
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpdateMembershipKey','
	SELECT UpdateMembership(:DEPLOYMENTID, :ADDRESS, :PORT, :GENERATION, :IAMALIVETIME, :STATUS, :SUSPECTTIMES, :VERSION) AS RESULT FROM DUAL
');
/
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertReminderRowKey','
	SELECT UpsertReminderRow(:SERVICEID, :GRAINHASH, :GRAINID, :REMINDERNAME, :STARTTIME, :PERIOD) AS Version FROM DUAL
');
/
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertReportClientMetricsKey','
	  SELECT UpsertReportClientMetrics(:DEPLOYMENTID, :HOSTNAME, :CPUUSAGE, :MEMORYUSAGE,
                                                        :SENDQUEUELENGTH, :RECEIVEQUEUELENGTH, :SENTMESSAGES,
                                                        :RECEIVEDMESSAGES, :CONNECTEDGATEWAYCOUNT, :CLIENTID, :ADDRESS) AS RESULT FROM DUAL
');
/
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertSiloMetricsKey','
  SELECT UpsertSiloMetrics(:DEPLOYMENTID, :HOSTNAME, :CPUUSAGE, :MEMORYUSAGE, :SENDQUEUELENGTH,
                                                :RECEIVEQUEUELENGTH, :SENTMESSAGES, :RECEIVEDMESSAGES, :ACTIVATIONCOUNT, :RECENTLYUSEDACTIVATIONCOUNT,
                                                :REQUESTQUEUELENGTH, :ISOVERLOADED, :CLIENTCOUNT, :ADDRESS,
                                                :PORT, :GENERATION, :GATEWAYADDRESS, :GATEWAYPORT, :SILOID) AS RESULT FROM DUAL
');
/
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'GatewaysQueryKey','
	SELECT Address, ProxyPort, Generation
    FROM OrleansMembershipTable
    WHERE DeploymentId = :DEPLOYMENTID AND :DEPLOYMENTID IS NOT NULL
      AND Status = :STATUS AND :STATUS IS NOT NULL
      AND ProxyPort > 0
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'MembershipReadRowKey','
	 SELECT v.DeploymentId, m.Address, m.Port, m.Generation, m.SiloName, m.HostName,
       m.Status, m.ProxyPort, m.SuspectTimes, m.StartTime, m.IAmAliveTime, v.Version
	FROM
		OrleansMembershipVersionTable v
		LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
		AND Address = :ADDRESS AND :ADDRESS IS NOT NULL
		AND Port = :PORT AND :PORT IS NOT NULL
		AND Generation = :GENERATION AND :GENERATION IS NOT NULL
	WHERE
		v.DeploymentId = :DEPLOYMENTID AND :DEPLOYMENTID IS NOT NULL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'MembershipReadAllKey','
	SELECT v.DeploymentId, m.Address, m.Port, m.Generation, m.SiloName, m.HostName, m.Status,
       m.ProxyPort, m.SuspectTimes, m.StartTime, m.IAmAliveTime, v.Version
	FROM
		OrleansMembershipVersionTable v
		LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
	WHERE
		v.DeploymentId = :DEPLOYMENTID AND :DEPLOYMENTID IS NOT NULL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteMembershipTableEntriesKey','
  BEGIN
    DELETE FROM OrleansMembershipTable
      WHERE DeploymentId = :DeploymentId AND :DeploymentId IS NOT NULL;
    DELETE FROM OrleansMembershipVersionTable
      WHERE DeploymentId = :DeploymentId AND :DeploymentId IS NOT NULL;
  END;
');
/
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadReminderRowsKey','
    SELECT GrainId, ReminderName, StartTime, Period, Version
    FROM OrleansRemindersTable
    WHERE
        ServiceId = :SERVICEID AND :SERVICEID IS NOT NULL
		AND GrainId = :GRAINID AND :GRAINID IS NOT NULL
');
/
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadReminderRowKey','
    SELECT GrainId, ReminderName, StartTime, Period, Version
    FROM OrleansRemindersTable
    WHERE
        ServiceId = :SERVICEID AND :SERVICEID IS NOT NULL
		AND GrainId = :GRAINID AND :GRAINID IS NOT NULL
        AND ReminderName = :REMINDERNAME AND :REMINDERNAME IS NOT NULL
');
/
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadRangeRows1Key','
	SELECT GrainId, ReminderName, StartTime, Period, Version
    FROM OrleansRemindersTable
    WHERE
		ServiceId = :SERVICEID AND :SERVICEID IS NOT NULL
		AND GrainHash > :BEGINHASH AND :BEGINHASH IS NOT NULL
		AND GrainHash <= :ENDHASH AND :ENDHASH IS NOT NULL
');
/
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadRangeRows2Key','
	SELECT GrainId, ReminderName, StartTime, Period,Version
    FROM OrleansRemindersTable
    WHERE
		ServiceId = :SERVICEID AND :SERVICEID IS NOT NULL
		AND ((GrainHash > :BEGINHASH AND :BEGINHASH IS NOT NULL)
		OR (GrainHash <= :ENDHASH AND :ENDHASH IS NOT NULL))
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertOrleansStatisticsKey','
BEGIN
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
	SELECT :DeploymentId, :Id, :HostName, :Name, :IsValueDelta, :StatValue, :Statistic FROM DUAL;
END;
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteReminderRowKey','
	SELECT DeleteReminderRow(:SERVICEID, :GRAINID, :REMINDERNAME, :VERSION) AS RESULT FROM DUAL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteReminderRowsKey','
	DELETE FROM OrleansRemindersTable
	WHERE ServiceId = :ServiceId AND :ServiceId IS NOT NULL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'WriteToStorageKey','
  SELECT WriteToStorage(:GRAINIDHASH, :GRAINIDN0, :GRAINIDN1, :GRAINTYPEHASH, :GRAINTYPESTRING,
                                             :GRAINIDEXTENSIONSTRING, :SERVICEID, :GRAINSTATEVERSION, :PAYLOADBINARY,
                                             :PAYLOADJSON, :PAYLOADXML) AS NewGrainStateVersion FROM DUAL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ClearStorageKey',
	'SELECT ClearStorage(:GRAINIDHASH, :GRAINIDN0, :GRAINIDN1, :GRAINTYPEHASH, :GRAINTYPESTRING,
                                             :GRAINIDEXTENSIONSTRING, :SERVICEID, :GRAINSTATEVERSION) AS VERSION FROM DUAL'
);
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadFromStorageKey',
	'
     SELECT PayloadBinary, PayloadXml, PayloadJson, Version
     FROM Storage
     WHERE GrainIdHash = :GRAINIDHASH AND :GRAINIDHASH IS NOT NULL
       AND (GrainIdN0 = :GRAINIDN0 OR :GRAINIDN0 IS NULL)
       AND (GrainIdN1 = :GRAINIDN1 OR :GRAINIDN1 IS NULL)
       AND GrainTypeHash = :GRAINTYPEHASH AND :GRAINTYPEHASH IS NOT NULL
       AND (GrainTypeString = :GRAINTYPESTRING OR :GRAINTYPESTRING IS NULL)
       AND ((:GRAINIDEXTENSIONSTRING IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = :GRAINIDEXTENSIONSTRING) OR:GRAINIDEXTENSIONSTRING IS NULL AND GrainIdExtensionString IS NULL)
       AND ServiceId = :SERVICEID AND :SERVICEID IS NOT NULL'
);
/
COMMIT;








  
  






