ALTER TABLE OrleansMembershipTable ADD MetadataJson NCLOB;
/

CREATE OR REPLACE FUNCTION InsertMembership(PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_IAMALIVETIME IN TIMESTAMP, PARAM_SILONAME IN NVARCHAR2, PARAM_HOSTNAME IN NVARCHAR2, PARAM_ADDRESS IN VARCHAR2,
                                    PARAM_PORT IN NUMBER, PARAM_GENERATION IN NUMBER, PARAM_STARTTIME IN TIMESTAMP, PARAM_STATUS IN NUMBER, PARAM_PROXYPORT IN NUMBER, PARAM_METADATAJSON IN NCLOB, PARAM_VERSION IN NUMBER)
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
      MetadataJson,
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
      PARAM_METADATAJSON,
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
    rowcount := SQL%ROWCOUNT;
    UPDATE OrleansMembershipVersionTable
    SET Timestamp = sys_extract_utc(systimestamp),
        Version = Version + 1
    WHERE
        DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
        AND Version = PARAM_VERSION AND PARAM_VERSION IS NOT NULL
      AND rowcount > 0;
    rowcount := SQL%ROWCOUNT;
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
                                               PARAM_IAMALIVETIME IN TIMESTAMP, PARAM_STATUS IN NUMBER, PARAM_SUSPECTTIMES IN VARCHAR2, PARAM_METADATAJSON IN NCLOB, PARAM_VERSION IN NUMBER
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
        MetadataJson = PARAM_METADATAJSON,
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

UPDATE OrleansQuery
SET QueryText = 'SELECT INSERTMEMBERSHIP(:DeploymentId,:IAmAliveTime,:SiloName,:Hostname,:Address,:Port,:Generation,:StartTime,:Status,:ProxyPort,:MetadataJson,:Version) FROM DUAL'
WHERE QueryKey = 'InsertMembershipKey';
/

UPDATE OrleansQuery
SET QueryText = 'SELECT UpdateMembership(:DeploymentId, :Address, :Port, :Generation, :IAmAliveTime, :Status, :SuspectTimes, :MetadataJson, :Version) AS RESULT FROM DUAL'
WHERE QueryKey = 'UpdateMembershipKey';
/

UPDATE OrleansQuery
SET QueryText = 'SELECT v.DeploymentId, m.Address, m.Port, m.Generation, m.SiloName, m.HostName,
       m.Status, m.ProxyPort, m.SuspectTimes, m.MetadataJson, m.StartTime, m.IAmAliveTime, v.Version
    FROM
        OrleansMembershipVersionTable v
        LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
        AND Address = :Address AND :Address IS NOT NULL
        AND Port = :Port AND :Port IS NOT NULL
        AND Generation = :Generation AND :Generation IS NOT NULL
    WHERE
        v.DeploymentId = :DeploymentId AND :DeploymentId IS NOT NULL'
WHERE QueryKey = 'MembershipReadRowKey';
/

UPDATE OrleansQuery
SET QueryText = 'SELECT v.DeploymentId, m.Address, m.Port, m.Generation, m.SiloName, m.HostName, m.Status,
       m.ProxyPort, m.SuspectTimes, m.MetadataJson, m.StartTime, m.IAmAliveTime, v.Version
    FROM
        OrleansMembershipVersionTable v
        LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
    WHERE
        v.DeploymentId = :DeploymentId AND :DeploymentId IS NOT NULL'
WHERE QueryKey = 'MembershipReadAllKey';
/

COMMIT;
