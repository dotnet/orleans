DECLARE
    column_count NUMBER;
    temporary_column_count NUMBER;
    column_type VARCHAR2(128);
    column_nullable VARCHAR2(1);
BEGIN
    SELECT COUNT(*)
    INTO column_count
    FROM USER_TAB_COLUMNS
    WHERE TABLE_NAME = 'ORLEANSMEMBERSHIPTABLE'
      AND COLUMN_NAME = 'METADATAJSON';

    SELECT COUNT(*)
    INTO temporary_column_count
    FROM USER_TAB_COLUMNS
    WHERE TABLE_NAME = 'ORLEANSMEMBERSHIPTABLE'
      AND COLUMN_NAME = 'METADATAJSONV2';

    IF column_count = 0 AND temporary_column_count = 1 THEN
        EXECUTE IMMEDIATE 'ALTER TABLE OrleansMembershipTable RENAME COLUMN MetadataJsonV2 TO MetadataJson';
        column_count := 1;
        temporary_column_count := 0;
    ELSIF column_count = 0 THEN
        EXECUTE IMMEDIATE 'ALTER TABLE OrleansMembershipTable ADD MetadataJson NCLOB NULL';
        column_count := 1;
    END IF;

    SELECT DATA_TYPE, NULLABLE
    INTO column_type, column_nullable
    FROM USER_TAB_COLUMNS
    WHERE TABLE_NAME = 'ORLEANSMEMBERSHIPTABLE'
      AND COLUMN_NAME = 'METADATAJSON';

    IF column_type <> 'NCLOB' THEN
        IF temporary_column_count = 0 THEN
            EXECUTE IMMEDIATE 'ALTER TABLE OrleansMembershipTable ADD MetadataJsonV2 NCLOB NULL';
        END IF;
        EXECUTE IMMEDIATE 'UPDATE OrleansMembershipTable SET MetadataJsonV2 = TO_NCLOB(MetadataJson)';
        EXECUTE IMMEDIATE 'ALTER TABLE OrleansMembershipTable DROP COLUMN MetadataJson';
        EXECUTE IMMEDIATE 'ALTER TABLE OrleansMembershipTable RENAME COLUMN MetadataJsonV2 TO MetadataJson';
    ELSIF temporary_column_count = 1 THEN
        EXECUTE IMMEDIATE 'ALTER TABLE OrleansMembershipTable DROP COLUMN MetadataJsonV2';
    END IF;

    IF column_nullable = 'N' THEN
        EXECUTE IMMEDIATE 'ALTER TABLE OrleansMembershipTable MODIFY MetadataJson NULL';
    END IF;
END;
/

CREATE OR REPLACE FUNCTION InsertMembershipV2(PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_IAMALIVETIME IN TIMESTAMP, PARAM_SILONAME IN NVARCHAR2, PARAM_HOSTNAME IN NVARCHAR2, PARAM_ADDRESS IN VARCHAR2,
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

CREATE OR REPLACE FUNCTION UpdateMembershipV2(PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_ADDRESS IN VARCHAR2, PARAM_PORT IN NUMBER, PARAM_GENERATION IN NUMBER,
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

MERGE INTO OrleansQuery target
USING (SELECT 'InsertMembershipV2Key' QueryKey,
    'SELECT InsertMembershipV2(:DeploymentId,:IAmAliveTime,:SiloName,:Hostname,:Address,:Port,:Generation,:StartTime,:Status,:ProxyPort,:MetadataJson,:Version) FROM DUAL' QueryText
    FROM DUAL) source
ON (target.QueryKey = source.QueryKey)
WHEN MATCHED THEN UPDATE SET target.QueryText = source.QueryText
WHEN NOT MATCHED THEN INSERT (QueryKey, QueryText) VALUES (source.QueryKey, source.QueryText);
/

MERGE INTO OrleansQuery target
USING (SELECT 'UpdateMembershipV2Key' QueryKey,
    'SELECT UpdateMembershipV2(:DeploymentId, :Address, :Port, :Generation, :IAmAliveTime, :Status, :SuspectTimes, :MetadataJson, :Version) AS RESULT FROM DUAL' QueryText
    FROM DUAL) source
ON (target.QueryKey = source.QueryKey)
WHEN MATCHED THEN UPDATE SET target.QueryText = source.QueryText
WHEN NOT MATCHED THEN INSERT (QueryKey, QueryText) VALUES (source.QueryKey, source.QueryText);
/

MERGE INTO OrleansQuery target
USING (SELECT 'MembershipReadRowV2Key' QueryKey,
    'SELECT v.DeploymentId, m.Address, m.Port, m.Generation, m.SiloName, m.HostName,
       m.Status, m.ProxyPort, m.SuspectTimes, m.MetadataJson, m.StartTime, m.IAmAliveTime, v.Version
     FROM OrleansMembershipVersionTable v
     LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
       AND Address = :Address AND :Address IS NOT NULL
       AND Port = :Port AND :Port IS NOT NULL
       AND Generation = :Generation AND :Generation IS NOT NULL
     WHERE v.DeploymentId = :DeploymentId AND :DeploymentId IS NOT NULL' QueryText
    FROM DUAL) source
ON (target.QueryKey = source.QueryKey)
WHEN MATCHED THEN UPDATE SET target.QueryText = source.QueryText
WHEN NOT MATCHED THEN INSERT (QueryKey, QueryText) VALUES (source.QueryKey, source.QueryText);
/

MERGE INTO OrleansQuery target
USING (SELECT 'MembershipReadAllV2Key' QueryKey,
    'SELECT v.DeploymentId, m.Address, m.Port, m.Generation, m.SiloName, m.HostName, m.Status,
       m.ProxyPort, m.SuspectTimes, m.MetadataJson, m.StartTime, m.IAmAliveTime, v.Version
     FROM OrleansMembershipVersionTable v
     LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
     WHERE v.DeploymentId = :DeploymentId AND :DeploymentId IS NOT NULL' QueryText
    FROM DUAL) source
ON (target.QueryKey = source.QueryKey)
WHEN MATCHED THEN UPDATE SET target.QueryText = source.QueryText
WHEN NOT MATCHED THEN INSERT (QueryKey, QueryText) VALUES (source.QueryKey, source.QueryText);
/

COMMIT;
