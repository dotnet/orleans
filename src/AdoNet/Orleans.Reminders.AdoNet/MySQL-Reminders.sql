-- Orleans Reminders table - https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders
CREATE TABLE OrleansRemindersTable
(
    ServiceId NVARCHAR(150) NOT NULL,
    GrainId VARCHAR(150) NOT NULL,
    ReminderName NVARCHAR(150) NOT NULL,
    StartTime DATETIME NOT NULL,
    Period BIGINT NOT NULL,
    CronExpression NVARCHAR(200) NULL,
    CronTimeZoneId NVARCHAR(200) NULL,
    NextDueUtc DATETIME NULL,
    LastFireUtc DATETIME NULL,
    Priority TINYINT NOT NULL DEFAULT 0,
    Action TINYINT NOT NULL DEFAULT 0,
    GrainHash INT NOT NULL,
    Version INT NOT NULL,

    CONSTRAINT PK_RemindersTable_ServiceId_GrainId_ReminderName PRIMARY KEY(ServiceId, GrainId, ReminderName)
);

CREATE INDEX IX_RemindersTable_NextDueUtc_Priority
ON OrleansRemindersTable(ServiceId, NextDueUtc, Priority);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpsertReminderRowKey','
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
    VALUES
    (
        @ServiceId,
        @GrainId,
        @ReminderName,
        @StartTime,
        @Period,
        @CronExpression,
        @CronTimeZoneId,
        @NextDueUtc,
        @LastFireUtc,
        @Priority,
        @Action,
        @GrainHash,
        last_insert_id(0)
    )
    ON DUPLICATE KEY
    UPDATE
        StartTime = @StartTime,
        Period = @Period,
        CronExpression = @CronExpression,
        CronTimeZoneId = @CronTimeZoneId,
        NextDueUtc = @NextDueUtc,
        LastFireUtc = @LastFireUtc,
        Priority = @Priority,
        Action = @Action,
        GrainHash = @GrainHash,
        Version = last_insert_id(Version+1);


    SELECT last_insert_id() AS Version;
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

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'DeleteReminderRowKey','
    DELETE FROM OrleansRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND GrainId = @GrainId AND @GrainId IS NOT NULL
        AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL
        AND Version = @Version AND @Version IS NOT NULL;
    SELECT ROW_COUNT();
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'DeleteReminderRowsKey','
    DELETE FROM OrleansRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL;
');
