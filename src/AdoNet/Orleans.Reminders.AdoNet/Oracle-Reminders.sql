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

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpsertReminderRowKey','
    SELECT UpsertReminderRow(:ServiceId, :GrainHash, :GrainId, :ReminderName, :StartTime, :Period) AS Version FROM DUAL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ReadReminderRowsKey','
    SELECT GrainId, ReminderName, StartTime, Period, Version
    FROM OrleansRemindersTable
    WHERE
        ServiceId = :ServiceId AND :ServiceId IS NOT NULL
        AND GrainId = :GrainId AND :GrainId IS NOT NULL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ReadReminderRowKey','
    SELECT GrainId, ReminderName, StartTime, Period, Version
    FROM OrleansRemindersTable
    WHERE
        ServiceId = :ServiceId AND :ServiceId IS NOT NULL
        AND GrainId = :GrainId AND :GrainId IS NOT NULL
        AND ReminderName = :ReminderName AND :ReminderName IS NOT NULL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ReadRangeRows1Key','
    SELECT GrainId, ReminderName, StartTime, Period, Version
    FROM OrleansRemindersTable
    WHERE
        ServiceId = :ServiceId AND :ServiceId IS NOT NULL
        AND GrainHash > :BeginHash AND :BeginHash IS NOT NULL
        AND GrainHash <= :EndHash AND :EndHash IS NOT NULL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ReadRangeRows2Key','
    SELECT GrainId, ReminderName, StartTime, Period,Version
    FROM OrleansRemindersTable
    WHERE
        ServiceId = :ServiceId AND :ServiceId IS NOT NULL
        AND ((GrainHash > :BeginHash AND :BeginHash IS NOT NULL)
        OR (GrainHash <= :EndHash AND :EndHash IS NOT NULL))
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'DeleteReminderRowKey','
    SELECT DeleteReminderRow(:ServiceId, :GrainId, :ReminderName, :Version) AS RESULT FROM DUAL
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

COMMIT;
