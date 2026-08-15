-- Orleans Reminders table - https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders
CREATE TABLE "ORLEANSADVANCEDREMINDERSTABLE"
(
    "SERVICEID" NVARCHAR2(150) NOT NULL ENABLE,
    "GRAINID" VARCHAR2(150) NOT NULL,
    "REMINDERNAME" NVARCHAR2(150) NOT NULL,
    "STARTTIME" TIMESTAMP(6) NOT NULL ENABLE,
    "PERIOD" NUMBER(19,0) NULL,
    "CRONEXPRESSION" NVARCHAR2(200) NULL,
    "CRONTIMEZONEID" NVARCHAR2(200) NULL,
    "NEXTDUEUTC" TIMESTAMP(6) NULL,
    "LASTFIREUTC" TIMESTAMP(6) NULL,
    "SCHEDULEID" VARCHAR2(64) NULL,
    "JOBID" VARCHAR2(64) NULL,
    "JOBSHARDID" VARCHAR2(150) NULL,
    "PRIORITY" NUMBER(3,0) DEFAULT 0 NOT NULL,
    "ACTION" NUMBER(3,0) DEFAULT 0 NOT NULL,
    "GRAINHASH" INT NOT NULL,
    "VERSION" INT NOT NULL,

    CONSTRAINT PK_ADVANCED_REMINDERS PRIMARY KEY(SERVICEID, GRAINID, REMINDERNAME)
);
/
CREATE INDEX IX_ADV_REM_NEXTDUE_PRIORITY ON ORLEANSADVANCEDREMINDERSTABLE(SERVICEID, NEXTDUEUTC, PRIORITY);
/
CREATE INDEX IX_ADV_REM_SERVICE_HASH ON ORLEANSADVANCEDREMINDERSTABLE(SERVICEID, GRAINHASH);
/

CREATE OR REPLACE FUNCTION UpsertAdvancedReminderRow(PARAM_SERVICEID IN NVARCHAR2, PARAM_GRAINHASH IN INT, PARAM_GRAINID IN VARCHAR2, PARAM_REMINDERNAME IN NVARCHAR2,
                                                PARAM_STARTTIME IN TIMESTAMP, PARAM_PERIOD IN NUMBER, PARAM_CRONEXPRESSION IN NVARCHAR2,
                                                PARAM_CRONTIMEZONEID IN NVARCHAR2, PARAM_NEXTDUEUTC IN TIMESTAMP, PARAM_LASTFIREUTC IN TIMESTAMP,
                                                PARAM_SCHEDULEID IN VARCHAR2, PARAM_JOBID IN VARCHAR2, PARAM_JOBSHARDID IN VARCHAR2,
                                                PARAM_PRIORITY IN NUMBER, PARAM_ACTION IN NUMBER, PARAM_VERSION IN NUMBER)
RETURN NUMBER IS
  currentVersion NUMBER := NULL;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    IF PARAM_VERSION = -1 THEN
      BEGIN
        INSERT INTO ORLEANSADVANCEDREMINDERSTABLE
          (ServiceId, GrainId, ReminderName, StartTime, Period, CronExpression, CronTimeZoneId, NextDueUtc, LastFireUtc, ScheduleId, JobId, JobShardId, Priority, Action, GrainHash, Version)
        VALUES
          (PARAM_SERVICEID, PARAM_GRAINID, PARAM_REMINDERNAME, PARAM_STARTTIME, PARAM_PERIOD, PARAM_CRONEXPRESSION, PARAM_CRONTIMEZONEID, PARAM_NEXTDUEUTC, PARAM_LASTFIREUTC, PARAM_SCHEDULEID, PARAM_JOBID, PARAM_JOBSHARDID, PARAM_PRIORITY, PARAM_ACTION, PARAM_GRAINHASH, 0);
        currentVersion := 0;
      EXCEPTION
        WHEN DUP_VAL_ON_INDEX THEN
          currentVersion := NULL;
      END;
    ELSE
      UPDATE ORLEANSADVANCEDREMINDERSTABLE
      SET StartTime = PARAM_STARTTIME,
          Period = PARAM_PERIOD,
          CronExpression = PARAM_CRONEXPRESSION,
          CronTimeZoneId = PARAM_CRONTIMEZONEID,
          NextDueUtc = PARAM_NEXTDUEUTC,
          LastFireUtc = PARAM_LASTFIREUTC,
          ScheduleId = PARAM_SCHEDULEID,
          JobId = PARAM_JOBID,
          JobShardId = PARAM_JOBSHARDID,
          Priority = PARAM_PRIORITY,
          Action = PARAM_ACTION,
          GrainHash = PARAM_GRAINHASH,
          Version = Version + 1
      WHERE ServiceId = PARAM_SERVICEID
        AND GrainId = PARAM_GRAINID
        AND ReminderName = PARAM_REMINDERNAME
        AND Version = PARAM_VERSION
      RETURNING Version INTO currentVersion;
    END IF;

    COMMIT;
    RETURN(currentVersion);
  END;
/

CREATE OR REPLACE FUNCTION DeleteAdvancedReminderRow(PARAM_SERVICEID IN NVARCHAR2, PARAM_GRAINID IN VARCHAR2, PARAM_REMINDERNAME IN NVARCHAR2,
                                                PARAM_VERSION IN NUMBER)
RETURN NUMBER IS
  rowcount NUMBER;
  PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    DELETE FROM ORLEANSADVANCEDREMINDERSTABLE
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
    'AdvancedRemindersUpsertReminderRowKey','
    SELECT Version
    FROM (
      SELECT UpsertAdvancedReminderRow(:ServiceId, :GrainHash, :GrainId, :ReminderName, :StartTime, :Period, :CronExpression, :CronTimeZoneId, :NextDueUtc, :LastFireUtc, :ScheduleId, :JobId, :JobShardId, :Priority, :Action, :Version) AS Version
      FROM DUAL
    )
    WHERE Version IS NOT NULL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersReadReminderRowsKey','
    SELECT GrainId, ReminderName, StartTime, Period, CronExpression, CronTimeZoneId, NextDueUtc, LastFireUtc, ScheduleId, JobId, JobShardId, Priority, Action, Version
    FROM ORLEANSADVANCEDREMINDERSTABLE
    WHERE
        ServiceId = :ServiceId AND :ServiceId IS NOT NULL
        AND GrainId = :GrainId AND :GrainId IS NOT NULL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersReadReminderRowKey','
    SELECT GrainId, ReminderName, StartTime, Period, CronExpression, CronTimeZoneId, NextDueUtc, LastFireUtc, ScheduleId, JobId, JobShardId, Priority, Action, Version
    FROM ORLEANSADVANCEDREMINDERSTABLE
    WHERE
        ServiceId = :ServiceId AND :ServiceId IS NOT NULL
        AND GrainId = :GrainId AND :GrainId IS NOT NULL
        AND ReminderName = :ReminderName AND :ReminderName IS NOT NULL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersReadRangeRows1Key','
    SELECT GrainId, ReminderName, StartTime, Period, CronExpression, CronTimeZoneId, NextDueUtc, LastFireUtc, ScheduleId, JobId, JobShardId, Priority, Action, Version
    FROM ORLEANSADVANCEDREMINDERSTABLE
    WHERE
        ServiceId = :ServiceId AND :ServiceId IS NOT NULL
        AND GrainHash > :BeginHash AND :BeginHash IS NOT NULL
        AND GrainHash <= :EndHash AND :EndHash IS NOT NULL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersReadRangeRows2Key','
    SELECT GrainId, ReminderName, StartTime, Period, CronExpression, CronTimeZoneId, NextDueUtc, LastFireUtc, ScheduleId, JobId, JobShardId, Priority, Action, Version
    FROM ORLEANSADVANCEDREMINDERSTABLE
    WHERE
        ServiceId = :ServiceId AND :ServiceId IS NOT NULL
        AND ((GrainHash > :BeginHash AND :BeginHash IS NOT NULL)
        OR (GrainHash <= :EndHash AND :EndHash IS NOT NULL))
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersDeleteReminderRowKey','
    SELECT DeleteAdvancedReminderRow(:ServiceId, :GrainId, :ReminderName, :Version) AS RESULT FROM DUAL
');
/

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersDeleteReminderRowsKey','
    DELETE FROM ORLEANSADVANCEDREMINDERSTABLE
    WHERE ServiceId = :ServiceId AND :ServiceId IS NOT NULL
');
/

COMMIT;
