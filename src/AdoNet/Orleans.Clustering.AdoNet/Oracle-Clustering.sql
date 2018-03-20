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
    SELECT UpdateIAmAlivetime(:DeploymentId, :Address, :Port, :Generation, :IAmAliveTime) AS RESULT FROM DUAL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'InsertMembershipVersionKey','
    SELECT InsertMembershipVersion(:DeploymentId) AS RESULT FROM DUAL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'InsertMembershipKey','
    SELECT INSERTMEMBERSHIP(:DeploymentId,:IAmAliveTime,:SiloName,:Hostname,:Address,:Port,:Generation,:StartTime,:Status,:ProxyPort,:Version) FROM DUAL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpdateMembershipKey','
    SELECT UpdateMembership(:DeploymentId, :Address, :Port, :Generation, :IAmAliveTime, :Status, :SuspectTimes, :Version) AS RESULT FROM DUAL
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
        AND Address = :Address AND :Address IS NOT NULL
        AND Port = :Port AND :Port IS NOT NULL
        AND Generation = :Generation AND :Generation IS NOT NULL
    WHERE
        v.DeploymentId = :DeploymentId AND :DeploymentId IS NOT NULL
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
        v.DeploymentId = :DeploymentId AND :DeploymentId IS NOT NULL
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
    'GatewaysQueryKey','
    SELECT Address, ProxyPort, Generation
    FROM OrleansMembershipTable
    WHERE DeploymentId = :DeploymentId AND :DeploymentId IS NOT NULL
      AND Status = :Status AND :Status IS NOT NULL
      AND ProxyPort > 0
');
/

COMMIT;
