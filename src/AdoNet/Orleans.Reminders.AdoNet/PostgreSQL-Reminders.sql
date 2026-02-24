-- Orleans Reminders table - https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders
CREATE TABLE OrleansRemindersTable
(
    ServiceId varchar(150) NOT NULL,
    GrainId varchar(150) NOT NULL,
    ReminderName varchar(150) NOT NULL,
    StartTime timestamptz(3) NOT NULL,
    Period bigint NOT NULL,
    CronExpression varchar(200) NULL,
    CronTimeZoneId varchar(200) NULL,
    NextDueUtc timestamptz(3) NULL,
    LastFireUtc timestamptz(3) NULL,
    Priority smallint NOT NULL DEFAULT 0,
    Action smallint NOT NULL DEFAULT 0,
    GrainHash integer NOT NULL,
    Version integer NOT NULL,

    CONSTRAINT PK_RemindersTable_ServiceId_GrainId_ReminderName PRIMARY KEY(ServiceId, GrainId, ReminderName)
);

CREATE INDEX IX_RemindersTable_NextDueUtc_Priority
    ON OrleansRemindersTable(ServiceId, NextDueUtc, Priority);

CREATE FUNCTION upsert_reminder_row(
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

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpsertReminderRowKey','
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
');

CREATE FUNCTION delete_reminder_row(
    ServiceIdArg    OrleansRemindersTable.ServiceId%TYPE,
    GrainIdArg      OrleansRemindersTable.GrainId%TYPE,
    ReminderNameArg OrleansRemindersTable.ReminderName%TYPE,
    VersionArg      OrleansRemindersTable.Version%TYPE
)
  RETURNS TABLE(row_count integer) AS
$func$
DECLARE
    RowCountVar int := 0;
BEGIN


    DELETE FROM OrleansRemindersTable
    WHERE
        ServiceId = ServiceIdArg AND ServiceIdArg IS NOT NULL
        AND GrainId = GrainIdArg AND GrainIdArg IS NOT NULL
        AND ReminderName = ReminderNameArg AND ReminderNameArg IS NOT NULL
        AND Version = VersionArg AND VersionArg IS NOT NULL;

    GET DIAGNOSTICS RowCountVar = ROW_COUNT;

    RETURN QUERY SELECT RowCountVar;

END
$func$ LANGUAGE plpgsql;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'DeleteReminderRowKey','
    SELECT * FROM delete_reminder_row(
        @ServiceId,
        @GrainId,
        @ReminderName,
        @Version
    );
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'DeleteReminderRowsKey','
    DELETE FROM OrleansRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL;
');
