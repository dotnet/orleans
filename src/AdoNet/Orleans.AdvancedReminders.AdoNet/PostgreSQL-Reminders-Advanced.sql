-- Orleans Reminders table - https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders
CREATE TABLE OrleansAdvancedRemindersTable
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
    ScheduleId varchar(128) NULL,
    JobId varchar(64) NULL,
    JobShardId varchar(150) NULL,
    Priority smallint NOT NULL DEFAULT 0,
    Action smallint NOT NULL DEFAULT 0,
    GrainHash integer NOT NULL,
    Version integer NOT NULL,

    CONSTRAINT PK_AdvancedReminders_ServiceId_GrainId_ReminderName PRIMARY KEY(ServiceId, GrainId, ReminderName)
);

CREATE INDEX IX_AdvancedReminders_NextDueUtc_Priority
    ON OrleansAdvancedRemindersTable(ServiceId, NextDueUtc, Priority);

CREATE INDEX IX_AdvancedReminders_ServiceId_GrainHash
    ON OrleansAdvancedRemindersTable(ServiceId, GrainHash);

CREATE FUNCTION upsert_advanced_reminder_row(
    ServiceIdArg    OrleansAdvancedRemindersTable.ServiceId%TYPE,
    GrainIdArg      OrleansAdvancedRemindersTable.GrainId%TYPE,
    ReminderNameArg OrleansAdvancedRemindersTable.ReminderName%TYPE,
    StartTimeArg    OrleansAdvancedRemindersTable.StartTime%TYPE,
    PeriodArg       OrleansAdvancedRemindersTable.Period%TYPE,
    CronExpressionArg OrleansAdvancedRemindersTable.CronExpression%TYPE,
    CronTimeZoneIdArg OrleansAdvancedRemindersTable.CronTimeZoneId%TYPE,
    NextDueUtcArg   OrleansAdvancedRemindersTable.NextDueUtc%TYPE,
    LastFireUtcArg  OrleansAdvancedRemindersTable.LastFireUtc%TYPE,
    ScheduleIdArg   OrleansAdvancedRemindersTable.ScheduleId%TYPE,
    JobIdArg        OrleansAdvancedRemindersTable.JobId%TYPE,
    JobShardIdArg   OrleansAdvancedRemindersTable.JobShardId%TYPE,
    PriorityArg     OrleansAdvancedRemindersTable.Priority%TYPE,
    ActionArg       OrleansAdvancedRemindersTable.Action%TYPE,
    GrainHashArg    OrleansAdvancedRemindersTable.GrainHash%TYPE,
    ExpectedVersionArg OrleansAdvancedRemindersTable.Version%TYPE
  )
  RETURNS TABLE(version integer) AS
$func$
DECLARE
    VersionVar int := NULL;
BEGIN
    IF ExpectedVersionArg = -1 THEN
        INSERT INTO OrleansAdvancedRemindersTable AS reminder
            (ServiceId, GrainId, ReminderName, StartTime, Period, CronExpression, CronTimeZoneId,
             NextDueUtc, LastFireUtc, ScheduleId, JobId, JobShardId, Priority, Action, GrainHash, Version)
        VALUES
            (ServiceIdArg, GrainIdArg, ReminderNameArg, StartTimeArg, PeriodArg, CronExpressionArg, CronTimeZoneIdArg,
             NextDueUtcArg, LastFireUtcArg, ScheduleIdArg, JobIdArg, JobShardIdArg, PriorityArg, ActionArg, GrainHashArg, 0)
        ON CONFLICT (ServiceId, GrainId, ReminderName) DO NOTHING
        RETURNING reminder.Version INTO VersionVar;
    ELSE
        UPDATE OrleansAdvancedRemindersTable AS reminder
        SET StartTime = StartTimeArg,
            Period = PeriodArg,
            CronExpression = CronExpressionArg,
            CronTimeZoneId = CronTimeZoneIdArg,
            NextDueUtc = NextDueUtcArg,
            LastFireUtc = LastFireUtcArg,
            ScheduleId = ScheduleIdArg,
            JobId = JobIdArg,
            JobShardId = JobShardIdArg,
            Priority = PriorityArg,
            Action = ActionArg,
            GrainHash = GrainHashArg,
            Version = reminder.Version + 1
        WHERE reminder.ServiceId = ServiceIdArg
          AND reminder.GrainId = GrainIdArg
          AND reminder.ReminderName = ReminderNameArg
          AND reminder.Version = ExpectedVersionArg
        RETURNING reminder.Version INTO VersionVar;
    END IF;

    RETURN QUERY SELECT VersionVar AS version WHERE VersionVar IS NOT NULL;

END
$func$ LANGUAGE plpgsql;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersUpsertReminderRowKey','
    SELECT * FROM upsert_advanced_reminder_row(
        @ServiceId,
        @GrainId,
        @ReminderName,
        @StartTime,
        @Period::bigint,
        @CronExpression,
        @CronTimeZoneId,
        @NextDueUtc,
        @LastFireUtc,
        @ScheduleId,
        @JobId,
        @JobShardId,
        @Priority::smallint,
        @Action::smallint,
        @GrainHash,
        @Version
    );
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersReadReminderRowsKey','
    SELECT
        GrainId,
        ReminderName,
        StartTime,
        Period,
        CronExpression,
        CronTimeZoneId,
        NextDueUtc,
        LastFireUtc,
        ScheduleId,
        JobId,
        JobShardId,
        Priority,
        Action,
        Version
    FROM OrleansAdvancedRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND GrainId = @GrainId AND @GrainId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersReadReminderRowKey','
    SELECT
        GrainId,
        ReminderName,
        StartTime,
        Period,
        CronExpression,
        CronTimeZoneId,
        NextDueUtc,
        LastFireUtc,
        ScheduleId,
        JobId,
        JobShardId,
        Priority,
        Action,
        Version
    FROM OrleansAdvancedRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND GrainId = @GrainId AND @GrainId IS NOT NULL
        AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersReadRangeRows1Key','
    SELECT
        GrainId,
        ReminderName,
        StartTime,
        Period,
        CronExpression,
        CronTimeZoneId,
        NextDueUtc,
        LastFireUtc,
        ScheduleId,
        JobId,
        JobShardId,
        Priority,
        Action,
        Version
    FROM OrleansAdvancedRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND GrainHash > @BeginHash AND @BeginHash IS NOT NULL
        AND GrainHash <= @EndHash AND @EndHash IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersReadRangeRows2Key','
    SELECT
        GrainId,
        ReminderName,
        StartTime,
        Period,
        CronExpression,
        CronTimeZoneId,
        NextDueUtc,
        LastFireUtc,
        ScheduleId,
        JobId,
        JobShardId,
        Priority,
        Action,
        Version
    FROM OrleansAdvancedRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND ((GrainHash > @BeginHash AND @BeginHash IS NOT NULL)
        OR (GrainHash <= @EndHash AND @EndHash IS NOT NULL));
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersReadRangeRows1PagedKey','
    SELECT GrainId, ReminderName, StartTime, Period, CronExpression, CronTimeZoneId, NextDueUtc, LastFireUtc, ScheduleId, JobId, JobShardId, Priority, Action, Version
    FROM OrleansAdvancedRemindersTable
    WHERE ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND GrainHash > @BeginHash AND @BeginHash IS NOT NULL
        AND GrainHash <= @EndHash AND @EndHash IS NOT NULL
        AND (@HasCursor = 0
            OR GrainHash > @CursorHash
            OR (GrainHash = @CursorHash AND GrainId > @CursorGrainId)
            OR (GrainHash = @CursorHash AND GrainId = @CursorGrainId AND ReminderName > @CursorReminderName))
    ORDER BY GrainHash, GrainId, ReminderName
    LIMIT @PageSize;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersReadRangeRows2PagedKey','
    SELECT GrainId, ReminderName, StartTime, Period, CronExpression, CronTimeZoneId, NextDueUtc, LastFireUtc, ScheduleId, JobId, JobShardId, Priority, Action, Version
    FROM OrleansAdvancedRemindersTable
    WHERE ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND ((GrainHash > @BeginHash AND @BeginHash IS NOT NULL)
        OR (GrainHash <= @EndHash AND @EndHash IS NOT NULL))
        AND (@HasCursor = 0
            OR GrainHash > @CursorHash
            OR (GrainHash = @CursorHash AND GrainId > @CursorGrainId)
            OR (GrainHash = @CursorHash AND GrainId = @CursorGrainId AND ReminderName > @CursorReminderName))
    ORDER BY GrainHash, GrainId, ReminderName
    LIMIT @PageSize;
');

CREATE FUNCTION delete_advanced_reminder_row(
    ServiceIdArg    OrleansAdvancedRemindersTable.ServiceId%TYPE,
    GrainIdArg      OrleansAdvancedRemindersTable.GrainId%TYPE,
    ReminderNameArg OrleansAdvancedRemindersTable.ReminderName%TYPE,
    VersionArg      OrleansAdvancedRemindersTable.Version%TYPE
)
  RETURNS TABLE(row_count integer) AS
$func$
DECLARE
    RowCountVar int := 0;
BEGIN


    DELETE FROM OrleansAdvancedRemindersTable
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
    'AdvancedRemindersDeleteReminderRowKey','
    SELECT * FROM delete_advanced_reminder_row(
        @ServiceId,
        @GrainId,
        @ReminderName,
        @Version
    );
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersDeleteReminderRowsKey','
    DELETE FROM OrleansAdvancedRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL;
');
