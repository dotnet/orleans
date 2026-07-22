-- Orleans Reminders table - https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders
CREATE TABLE OrleansAdvancedRemindersTable
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
    ScheduleId VARCHAR(64) NULL,
    JobId VARCHAR(64) NULL,
    JobShardId VARCHAR(150) NULL,
    Priority TINYINT NOT NULL DEFAULT 0,
    Action TINYINT NOT NULL DEFAULT 0,
    GrainHash INT NOT NULL,
    Version INT NOT NULL,

    CONSTRAINT PK_AdvancedReminders_ServiceId_GrainId_ReminderName PRIMARY KEY(ServiceId, GrainId, ReminderName)
);

CREATE INDEX IX_RemindersTable_NextDueUtc_Priority
ON OrleansAdvancedRemindersTable(ServiceId, NextDueUtc, Priority);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'AdvancedRemindersUpsertReminderRowKey','
    SET @NewVersion := NULL;
    START TRANSACTION;

    UPDATE OrleansAdvancedRemindersTable
    SET
        StartTime = @StartTime,
        Period = @Period,
        CronExpression = @CronExpression,
        CronTimeZoneId = @CronTimeZoneId,
        NextDueUtc = @NextDueUtc,
        LastFireUtc = @LastFireUtc,
        ScheduleId = @ScheduleId,
        JobId = @JobId,
        JobShardId = @JobShardId,
        Priority = @Priority,
        Action = @Action,
        GrainHash = @GrainHash,
        Version = LAST_INSERT_ID(Version + 1)
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND GrainId = @GrainId AND @GrainId IS NOT NULL
        AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL
        AND Version = @Version
        AND @Version >= 0;

    SET @NewVersion := IF(ROW_COUNT() = 1, LAST_INSERT_ID(), NULL);

    INSERT IGNORE INTO OrleansAdvancedRemindersTable
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
        ScheduleId,
        JobId,
        JobShardId,
        Priority,
        Action,
        GrainHash,
        Version
    )
    SELECT
        @ServiceId,
        @GrainId,
        @ReminderName,
        @StartTime,
        @Period,
        @CronExpression,
        @CronTimeZoneId,
        @NextDueUtc,
        @LastFireUtc,
        @ScheduleId,
        @JobId,
        @JobShardId,
        @Priority,
        @Action,
        @GrainHash,
        0
    WHERE @Version = -1;

    SET @NewVersion := IF(ROW_COUNT() = 1, 0, @NewVersion);

    SELECT @NewVersion AS Version WHERE @NewVersion IS NOT NULL;
    COMMIT;
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
    'AdvancedRemindersDeleteReminderRowKey','
    DELETE FROM OrleansAdvancedRemindersTable
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
    'AdvancedRemindersDeleteReminderRowsKey','
    DELETE FROM OrleansAdvancedRemindersTable
    WHERE
        ServiceId = @ServiceId AND @ServiceId IS NOT NULL;
');
