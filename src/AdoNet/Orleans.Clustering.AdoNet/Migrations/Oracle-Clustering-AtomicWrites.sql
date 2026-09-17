-- Updates membership writes and adds captured-value Dead-row pruning.
-- Apply to an existing clustering database before starting the updated provider.
-- Existing table schemas, query parameters, and routine signatures are preserved.

CREATE OR REPLACE FUNCTION InsertMembership(PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_IAMALIVETIME IN TIMESTAMP, PARAM_SILONAME IN NVARCHAR2, PARAM_HOSTNAME IN NVARCHAR2, PARAM_ADDRESS IN VARCHAR2,
                                    PARAM_PORT IN NUMBER, PARAM_GENERATION IN NUMBER, PARAM_STARTTIME IN TIMESTAMP, PARAM_STATUS IN NUMBER, PARAM_PROXYPORT IN NUMBER, PARAM_VERSION IN NUMBER)
  RETURN NUMBER IS
  rowcount NUMBER;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    UPDATE OrleansMembershipVersionTable
    SET Timestamp = sys_extract_utc(systimestamp),
        Version = Version + 1
    WHERE DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
      AND Version = PARAM_VERSION AND PARAM_VERSION IS NOT NULL AND Version < 2147483647;
    rowcount := SQL%ROWCOUNT;

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
    FROM DUAL WHERE rowcount > 0 AND NOT EXISTS
    (
      SELECT 1 FROM OrleansMembershipTable WHERE
        DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
        AND Address = PARAM_ADDRESS AND PARAM_ADDRESS IS NOT NULL
        AND Port = PARAM_PORT AND PARAM_PORT IS NOT NULL
        AND Generation = PARAM_GENERATION AND PARAM_GENERATION IS NOT NULL
    );
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
  EXCEPTION WHEN OTHERS THEN
    ROLLBACK;
    RAISE;
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
		AND Version = PARAM_VERSION AND PARAM_VERSION IS NOT NULL AND Version < 2147483647;
    rowcount := SQL%ROWCOUNT;
    UPDATE OrleansMembershipTable
      SET
        Status = PARAM_STATUS,
        SuspectTimes = PARAM_SUSPECTTIMES,
        IAmAliveTime = GREATEST(IAmAliveTime, PARAM_IAMALIVETIME)
      WHERE DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
        AND Address = PARAM_ADDRESS AND PARAM_ADDRESS IS NOT NULL
        AND Port = PARAM_PORT AND PARAM_PORT IS NOT NULL
        AND Generation = PARAM_GENERATION AND PARAM_GENERATION IS NOT NULL
        AND rowcount > 0;
    rowcount := SQL%ROWCOUNT;
    IF rowcount = 0 THEN
      ROLLBACK;
    ELSE
      COMMIT;
    END IF;
    RETURN(rowcount);
  EXCEPTION WHEN OTHERS THEN
    ROLLBACK;
    RAISE;
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
            IAmAliveTime = GREATEST(IAmAliveTime, PARAM_IAMALIVE)
        WHERE
            DeploymentId = PARAM_DEPLOYMENTID AND PARAM_DEPLOYMENTID IS NOT NULL
            AND Address = PARAM_ADDRESS AND PARAM_ADDRESS IS NOT NULL
            AND Port = PARAM_PORT AND PARAM_PORT IS NOT NULL
            AND Generation = PARAM_GENERATION AND PARAM_GENERATION IS NOT NULL;
      COMMIT;
      RETURN(0);
END;
/

UPDATE OrleansQuery SET QueryText = '
  BEGIN
    DELETE FROM OrleansMembershipTable
      WHERE DeploymentId = :DeploymentId
        AND :DeploymentId IS NOT NULL
        AND IAmAliveTime < :IAmAliveTime
        AND StartTime < :IAmAliveTime
        AND SuspectTimes IS NULL
        AND Status = 6;
  END;
'
WHERE QueryKey = 'CleanupDefunctSiloEntriesKey';
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT 'CleanupDefunctSiloEntryKey', '
    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = :DeploymentId AND Status = 6
        AND Address = :Address AND Port = :Port AND Generation = :Generation
        AND IAmAliveTime = :IAmAliveTime AND StartTime = :StartTime
        AND (SuspectTimes = :SuspectTimes OR (SuspectTimes IS NULL AND :SuspectTimes IS NULL))
'
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM OrleansQuery WHERE QueryKey = 'CleanupDefunctSiloEntryKey');
/

COMMIT;
