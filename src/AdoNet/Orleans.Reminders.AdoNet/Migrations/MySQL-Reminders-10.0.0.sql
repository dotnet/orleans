-- Run this migration for upgrading MySQL reminder tables created before 10.0.0.

SET @orleans_col_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'OrleansRemindersTable'
      AND COLUMN_NAME = 'CronExpression'
);
SET @orleans_col_sql = IF(
    @orleans_col_exists = 0,
    'ALTER TABLE OrleansRemindersTable ADD COLUMN CronExpression NVARCHAR(200) NULL',
    'SELECT 1');
PREPARE orleans_stmt FROM @orleans_col_sql;
EXECUTE orleans_stmt;
DEALLOCATE PREPARE orleans_stmt;

SET @orleans_col_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'OrleansRemindersTable'
      AND COLUMN_NAME = 'NextDueUtc'
);
SET @orleans_col_sql = IF(
    @orleans_col_exists = 0,
    'ALTER TABLE OrleansRemindersTable ADD COLUMN NextDueUtc DATETIME NULL',
    'SELECT 1');
PREPARE orleans_stmt FROM @orleans_col_sql;
EXECUTE orleans_stmt;
DEALLOCATE PREPARE orleans_stmt;

SET @orleans_col_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'OrleansRemindersTable'
      AND COLUMN_NAME = 'LastFireUtc'
);
SET @orleans_col_sql = IF(
    @orleans_col_exists = 0,
    'ALTER TABLE OrleansRemindersTable ADD COLUMN LastFireUtc DATETIME NULL',
    'SELECT 1');
PREPARE orleans_stmt FROM @orleans_col_sql;
EXECUTE orleans_stmt;
DEALLOCATE PREPARE orleans_stmt;

SET @orleans_col_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'OrleansRemindersTable'
      AND COLUMN_NAME = 'Priority'
);
SET @orleans_col_sql = IF(
    @orleans_col_exists = 0,
    'ALTER TABLE OrleansRemindersTable ADD COLUMN Priority TINYINT NOT NULL DEFAULT 1',
    'SELECT 1');
PREPARE orleans_stmt FROM @orleans_col_sql;
EXECUTE orleans_stmt;
DEALLOCATE PREPARE orleans_stmt;

SET @orleans_col_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'OrleansRemindersTable'
      AND COLUMN_NAME = 'Action'
);
SET @orleans_col_sql = IF(
    @orleans_col_exists = 0,
    'ALTER TABLE OrleansRemindersTable ADD COLUMN Action TINYINT NOT NULL DEFAULT 1',
    'SELECT 1');
PREPARE orleans_stmt FROM @orleans_col_sql;
EXECUTE orleans_stmt;
DEALLOCATE PREPARE orleans_stmt;

SET @orleans_idx_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'OrleansRemindersTable'
      AND INDEX_NAME = 'IX_RemindersTable_NextDueUtc_Priority'
);

SET @orleans_idx_sql = IF(
    @orleans_idx_exists = 0,
    'CREATE INDEX IX_RemindersTable_NextDueUtc_Priority ON OrleansRemindersTable(ServiceId, NextDueUtc, Priority)',
    'SELECT 1');

PREPARE orleans_stmt FROM @orleans_idx_sql;
EXECUTE orleans_stmt;
DEALLOCATE PREPARE orleans_stmt;

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
