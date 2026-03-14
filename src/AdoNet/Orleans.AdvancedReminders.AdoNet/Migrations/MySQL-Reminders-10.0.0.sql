-- Run this migration for upgrading MySQL reminder tables created before 10.0.0.

ALTER TABLE OrleansRemindersTable
    ADD COLUMN CronExpression NVARCHAR(200) NULL,
    ADD COLUMN CronTimeZoneId NVARCHAR(200) NULL,
    ADD COLUMN NextDueUtc DATETIME NULL,
    ADD COLUMN LastFireUtc DATETIME NULL,
    ADD COLUMN Priority TINYINT NOT NULL DEFAULT 0,
    ADD COLUMN Action TINYINT NOT NULL DEFAULT 0;

CREATE INDEX IX_RemindersTable_NextDueUtc_Priority ON OrleansRemindersTable(ServiceId, NextDueUtc, Priority);

UPDATE OrleansQuery
SET QueryText = '
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
