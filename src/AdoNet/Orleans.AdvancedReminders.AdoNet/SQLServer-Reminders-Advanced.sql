-- Orleans Reminders table - https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders
IF OBJECT_ID(N'[OrleansAdvancedRemindersTable]', 'U') IS NULL
CREATE TABLE OrleansAdvancedRemindersTable
(
	ServiceId NVARCHAR(150) NOT NULL,
	GrainId VARCHAR(150) NOT NULL,
	ReminderName NVARCHAR(150) NOT NULL,
	StartTime DATETIME2(3) NOT NULL,
	Period BIGINT NOT NULL,
	CronExpression NVARCHAR(200) NULL,
	CronTimeZoneId NVARCHAR(200) NULL,
	NextDueUtc DATETIME2(3) NULL,
	LastFireUtc DATETIME2(3) NULL,
	ScheduleId VARCHAR(64) NULL,
	JobId VARCHAR(64) NULL,
	JobShardId VARCHAR(150) NULL,
	Priority SMALLINT NOT NULL CONSTRAINT DF_OrleansAdvancedRemindersTable_Priority DEFAULT (0),
	Action TINYINT NOT NULL CONSTRAINT DF_OrleansAdvancedRemindersTable_Action DEFAULT (0),
	GrainHash INT NOT NULL,
	Version INT NOT NULL,

	CONSTRAINT PK_AdvancedReminders_ServiceId_GrainId_ReminderName PRIMARY KEY(ServiceId, GrainId, ReminderName)
);

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_RemindersTable_NextDueUtc_Priority'
      AND object_id = OBJECT_ID('OrleansAdvancedRemindersTable')
)
BEGIN
    CREATE INDEX IX_RemindersTable_NextDueUtc_Priority
    ON OrleansAdvancedRemindersTable(ServiceId, NextDueUtc, Priority);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_AdvancedReminders_ServiceId_GrainHash'
      AND object_id = OBJECT_ID('OrleansAdvancedRemindersTable')
)
BEGIN
    CREATE INDEX IX_AdvancedReminders_ServiceId_GrainHash
    ON OrleansAdvancedRemindersTable(ServiceId, GrainHash);
END;

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'AdvancedRemindersUpsertReminderRowKey',
	'DECLARE @NewVersion AS INT = NULL;
	SET NOCOUNT ON;
	IF @Version = -1
	BEGIN
		BEGIN TRY
			INSERT INTO OrleansAdvancedRemindersTable
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
				@ScheduleId,
				@JobId,
				@JobShardId,
				@Priority,
				@Action,
				@GrainHash,
				0
			);
			SET @NewVersion = 0;
		END TRY
		BEGIN CATCH
			IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
		END CATCH;
	END
	ELSE
	BEGIN
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
			@NewVersion = Version = Version + 1
		WHERE
			ServiceId = @ServiceId AND @ServiceId IS NOT NULL
			AND GrainId = @GrainId AND @GrainId IS NOT NULL
			AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL
			AND Version = @Version;
	END;
	SELECT @NewVersion AS Version WHERE @NewVersion IS NOT NULL;
	'
WHERE NOT EXISTS
(
    SELECT 1
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'AdvancedRemindersUpsertReminderRowKey'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'AdvancedRemindersReadReminderRowsKey',
	'SELECT
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
	'
WHERE NOT EXISTS
(
    SELECT 1
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'AdvancedRemindersReadReminderRowsKey'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'AdvancedRemindersReadReminderRowKey',
	'SELECT
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
	'
WHERE NOT EXISTS
(
    SELECT 1
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'AdvancedRemindersReadReminderRowKey'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'AdvancedRemindersReadRangeRows1Key',
	'SELECT
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
	'
WHERE NOT EXISTS
(
    SELECT 1
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'AdvancedRemindersReadRangeRows1Key'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'AdvancedRemindersReadRangeRows2Key',
	'SELECT
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
	'
WHERE NOT EXISTS
(
    SELECT 1
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'AdvancedRemindersReadRangeRows2Key'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'AdvancedRemindersDeleteReminderRowKey',
	'DELETE FROM OrleansAdvancedRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND GrainId = @GrainId AND @GrainId IS NOT NULL
		AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL
		AND Version = @Version AND @Version IS NOT NULL;
	SELECT @@ROWCOUNT;
	'
WHERE NOT EXISTS
(
    SELECT 1
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'AdvancedRemindersDeleteReminderRowKey'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'AdvancedRemindersDeleteReminderRowsKey',
	'DELETE FROM OrleansAdvancedRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL;
	'
WHERE NOT EXISTS
(
    SELECT 1
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'AdvancedRemindersDeleteReminderRowsKey'
);
