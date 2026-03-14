-- Run this migration for upgrading SQL Server reminder tables created before 10.0.0.

ALTER TABLE OrleansRemindersTable ADD CronExpression NVARCHAR(200) NULL;
ALTER TABLE OrleansRemindersTable ADD CronTimeZoneId NVARCHAR(200) NULL;
ALTER TABLE OrleansRemindersTable ADD NextDueUtc DATETIME2(3) NULL;
ALTER TABLE OrleansRemindersTable ADD LastFireUtc DATETIME2(3) NULL;
ALTER TABLE OrleansRemindersTable ADD Priority TINYINT NOT NULL CONSTRAINT DF_OrleansRemindersTable_Priority DEFAULT (0);
ALTER TABLE OrleansRemindersTable ADD Action TINYINT NOT NULL CONSTRAINT DF_OrleansRemindersTable_Action DEFAULT (0);

CREATE INDEX IX_RemindersTable_NextDueUtc_Priority
ON OrleansRemindersTable(ServiceId, NextDueUtc, Priority);

UPDATE OrleansQuery
SET QueryText = 'DECLARE @Version AS INT = 0;
	SET XACT_ABORT, NOCOUNT ON;
	BEGIN TRANSACTION;
	UPDATE OrleansRemindersTable WITH(UPDLOCK, ROWLOCK, HOLDLOCK)
	SET
		StartTime = @StartTime,
		Period = @Period,
		CronExpression = @CronExpression,
		CronTimeZoneId = @CronTimeZoneId,
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
		CronTimeZoneId,
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
		@CronTimeZoneId,
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
SET QueryText = 'SELECT
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
SET QueryText = 'SELECT
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
SET QueryText = 'SELECT
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
