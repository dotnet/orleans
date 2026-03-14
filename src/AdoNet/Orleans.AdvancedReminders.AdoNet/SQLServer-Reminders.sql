-- Orleans Reminders table - https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders
IF OBJECT_ID(N'[OrleansRemindersTable]', 'U') IS NULL
CREATE TABLE OrleansRemindersTable
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
	Priority TINYINT NOT NULL CONSTRAINT DF_OrleansRemindersTable_Priority DEFAULT (0),
	Action TINYINT NOT NULL CONSTRAINT DF_OrleansRemindersTable_Action DEFAULT (0),
	GrainHash INT NOT NULL,
	Version INT NOT NULL,

	CONSTRAINT PK_RemindersTable_ServiceId_GrainId_ReminderName PRIMARY KEY(ServiceId, GrainId, ReminderName)
);

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

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'UpsertReminderRowKey',
	'DECLARE @Version AS INT = 0;
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
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'UpsertReminderRowKey'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'ReadReminderRowsKey',
	'SELECT
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
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'ReadReminderRowsKey'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'ReadReminderRowKey',
	'SELECT
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
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'ReadReminderRowKey'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'ReadRangeRows1Key',
	'SELECT
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
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'ReadRangeRows1Key'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'ReadRangeRows2Key',
	'SELECT
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
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'ReadRangeRows2Key'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'DeleteReminderRowKey',
	'DELETE FROM OrleansRemindersTable
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
    WHERE oqt.[QueryKey] = 'DeleteReminderRowKey'
);    

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
	'DeleteReminderRowsKey',
	'DELETE FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL;
	'
WHERE NOT EXISTS 
( 
    SELECT 1 
    FROM OrleansQuery oqt
    WHERE oqt.[QueryKey] = 'DeleteReminderRowsKey'
);  
