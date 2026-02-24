-- Run this migration for upgrading PostgreSQL reminder tables created before 10.0.0.

ALTER TABLE OrleansRemindersTable
    ADD COLUMN CronExpression varchar(200),
    ADD COLUMN CronTimeZoneId varchar(200),
    ADD COLUMN NextDueUtc timestamptz(3),
    ADD COLUMN LastFireUtc timestamptz(3),
    ADD COLUMN Priority smallint NOT NULL DEFAULT 0,
    ADD COLUMN Action smallint NOT NULL DEFAULT 0;

CREATE INDEX IX_RemindersTable_NextDueUtc_Priority
    ON OrleansRemindersTable(ServiceId, NextDueUtc, Priority);

CREATE OR REPLACE FUNCTION upsert_reminder_row(
    ServiceIdArg    OrleansRemindersTable.ServiceId%TYPE,
    GrainIdArg      OrleansRemindersTable.GrainId%TYPE,
    ReminderNameArg OrleansRemindersTable.ReminderName%TYPE,
    StartTimeArg    OrleansRemindersTable.StartTime%TYPE,
    PeriodArg       OrleansRemindersTable.Period%TYPE,
    CronExpressionArg OrleansRemindersTable.CronExpression%TYPE,
    CronTimeZoneIdArg OrleansRemindersTable.CronTimeZoneId%TYPE,
    NextDueUtcArg   OrleansRemindersTable.NextDueUtc%TYPE,
    LastFireUtcArg  OrleansRemindersTable.LastFireUtc%TYPE,
    PriorityArg     OrleansRemindersTable.Priority%TYPE,
    ActionArg       OrleansRemindersTable.Action%TYPE,
    GrainHashArg    OrleansRemindersTable.GrainHash%TYPE
  )
  RETURNS TABLE(version integer) AS
$func$
DECLARE
    VersionVar int := 0;
BEGIN

    INSERT INTO OrleansRemindersTable
    (
        ServiceId,
        GrainId,
        ReminderName,
        StartTime,
        Period,
        CronExpression,
        CronTimeZoneId,
        NextDueUtc,
        LastFireUtc,
        Priority,
        Action,
        GrainHash,
        Version
    )
    SELECT
        ServiceIdArg,
        GrainIdArg,
        ReminderNameArg,
        StartTimeArg,
        PeriodArg,
        CronExpressionArg,
        CronTimeZoneIdArg,
        NextDueUtcArg,
        LastFireUtcArg,
        PriorityArg,
        ActionArg,
        GrainHashArg,
        0
    ON CONFLICT (ServiceId, GrainId, ReminderName)
        DO UPDATE SET
            StartTime = excluded.StartTime,
            Period = excluded.Period,
            CronExpression = excluded.CronExpression,
            CronTimeZoneId = excluded.CronTimeZoneId,
            NextDueUtc = excluded.NextDueUtc,
            LastFireUtc = excluded.LastFireUtc,
            Priority = excluded.Priority,
            Action = excluded.Action,
            GrainHash = excluded.GrainHash,
            Version = OrleansRemindersTable.Version + 1
    RETURNING
        OrleansRemindersTable.Version INTO STRICT VersionVar;

    RETURN QUERY SELECT VersionVar AS version;

END
$func$ LANGUAGE plpgsql;

UPDATE OrleansQuery
SET QueryText = '
    SELECT * FROM upsert_reminder_row(
        @ServiceId,
        @GrainId,
        @ReminderName,
        @StartTime,
        @Period::bigint,
        @CronExpression,
        @CronTimeZoneId,
        @NextDueUtc,
        @LastFireUtc,
        @Priority::smallint,
        @Action::smallint,
        @GrainHash
    );
'
WHERE QueryKey = 'UpsertReminderRowKey';

UPDATE OrleansQuery
SET QueryText = '
    SELECT
        GrainId,
        ReminderName,
        StartTime,
        Period,
        CronExpression,
        CronTimeZoneId,
        NextDueUtc,
        LastFireUtc,
        Priority,
        Action,
        Version
    FROM OrleansRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND GrainId = @GrainId AND @GrainId IS NOT NULL;
'
WHERE QueryKey = 'ReadReminderRowsKey';

UPDATE OrleansQuery
SET QueryText = '
    SELECT
        GrainId,
        ReminderName,
        StartTime,
        Period,
        CronExpression,
        CronTimeZoneId,
        NextDueUtc,
        LastFireUtc,
        Priority,
        Action,
        Version
    FROM OrleansRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND GrainId = @GrainId AND @GrainId IS NOT NULL
        AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL;
'
WHERE QueryKey = 'ReadReminderRowKey';

UPDATE OrleansQuery
SET QueryText = '
    SELECT
        GrainId,
        ReminderName,
        StartTime,
        Period,
        CronExpression,
        CronTimeZoneId,
        NextDueUtc,
        LastFireUtc,
        Priority,
        Action,
        Version
    FROM OrleansRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND GrainHash > @BeginHash AND @BeginHash IS NOT NULL
        AND GrainHash <= @EndHash AND @EndHash IS NOT NULL;
'
WHERE QueryKey = 'ReadRangeRows1Key';

UPDATE OrleansQuery
SET QueryText = '
    SELECT
        GrainId,
        ReminderName,
        StartTime,
        Period,
        CronExpression,
        CronTimeZoneId,
        NextDueUtc,
        LastFireUtc,
        Priority,
        Action,
        Version
    FROM OrleansRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND ((GrainHash > @BeginHash AND @BeginHash IS NOT NULL)
        OR (GrainHash <= @EndHash AND @EndHash IS NOT NULL));
'
WHERE QueryKey = 'ReadRangeRows2Key';
