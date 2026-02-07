-- Run this migration for upgrading SQL Server reminder tables created before 10.0.0.

IF COL_LENGTH('OrleansRemindersTable', 'CronExpression') IS NULL
BEGIN
    ALTER TABLE OrleansRemindersTable ADD CronExpression NVARCHAR(200) NULL;
END;

IF COL_LENGTH('OrleansRemindersTable', 'NextDueUtc') IS NULL
BEGIN
    ALTER TABLE OrleansRemindersTable ADD NextDueUtc DATETIME2(3) NULL;
END;

IF COL_LENGTH('OrleansRemindersTable', 'LastFireUtc') IS NULL
BEGIN
    ALTER TABLE OrleansRemindersTable ADD LastFireUtc DATETIME2(3) NULL;
END;

IF COL_LENGTH('OrleansRemindersTable', 'Priority') IS NULL
BEGIN
    ALTER TABLE OrleansRemindersTable ADD Priority TINYINT NOT NULL CONSTRAINT DF_OrleansRemindersTable_Priority DEFAULT (0);
END;

IF COL_LENGTH('OrleansRemindersTable', 'Action') IS NULL
BEGIN
    ALTER TABLE OrleansRemindersTable ADD Action TINYINT NOT NULL CONSTRAINT DF_OrleansRemindersTable_Action DEFAULT (0);
END;

DECLARE @priorityConstraintName SYSNAME;
DECLARE @actionConstraintName SYSNAME;

SELECT
    @priorityConstraintName = dc.name
FROM sys.default_constraints dc
INNER JOIN sys.columns c ON c.default_object_id = dc.object_id
INNER JOIN sys.tables t ON t.object_id = c.object_id
WHERE t.name = 'OrleansRemindersTable' AND c.name = 'Priority';

SELECT
    @actionConstraintName = dc.name
FROM sys.default_constraints dc
INNER JOIN sys.columns c ON c.default_object_id = dc.object_id
INNER JOIN sys.tables t ON t.object_id = c.object_id
WHERE t.name = 'OrleansRemindersTable' AND c.name = 'Action';

IF @priorityConstraintName IS NULL
BEGIN
    ALTER TABLE OrleansRemindersTable
        ADD CONSTRAINT DF_OrleansRemindersTable_Priority DEFAULT (0) FOR Priority;
END;

IF @actionConstraintName IS NULL
BEGIN
    ALTER TABLE OrleansRemindersTable
        ADD CONSTRAINT DF_OrleansRemindersTable_Action DEFAULT (0) FOR Action;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_RemindersTable_NextDueUtc_Priority'
      AND object_id = OBJECT_ID('OrleansRemindersTable')
)
BEGIN
    CREATE INDEX IX_RemindersTable_NextDueUtc_Priority
    ON OrleansRemindersTable(ServiceId, NextDueUtc, Priority);
END;

UPDATE OrleansQuery
SET QueryText = 'DECLARE @Version AS INT = 0;
	SET XACT_ABORT, NOCOUNT ON;
	BEGIN TRANSACTION;
	UPDATE OrleansRemindersTable WITH(UPDLOCK, ROWLOCK, HOLDLOCK)
	SET
		StartTime = @StartTime,
		Period = @Period,
		CronExpression = @CronExpression,
		NextDueUtc = @NextDueUtc,
		LastFireUtc = @LastFireUtc,
		Priority = @Priority,
		Action = @Action,
		GrainHash = @GrainHash,
		@Version = Version = Version + 1
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND GrainId = @GrainId AND @GrainId IS NOT NULL
		AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL;

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
	SELECT
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
		0
	WHERE
		@@ROWCOUNT=0;
	SELECT @Version AS Version;
	COMMIT TRANSACTION;
	'
WHERE QueryKey = 'UpsertReminderRowKey';

UPDATE OrleansQuery
SET QueryText = 'SELECT
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
SET QueryText = 'SELECT
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
SET QueryText = 'SELECT
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
SET QueryText = 'SELECT
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
