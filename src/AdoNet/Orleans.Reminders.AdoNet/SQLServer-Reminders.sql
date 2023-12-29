-- Orleans Reminders table - https://learn.microsoft.com/dotnet/orleans/grains/timers-and-reminders
IF OBJECT_ID(N'[OrleansRemindersTable]', 'U') IS NULL
CREATE TABLE OrleansRemindersTable
(
	ServiceId NVARCHAR(150) NOT NULL,
	GrainId VARCHAR(150) NOT NULL,
	ReminderName NVARCHAR(150) NOT NULL,
	StartTime DATETIME2(3) NOT NULL,
	Period BIGINT NOT NULL,
	GrainHash INT NOT NULL,
	Version INT NOT NULL,

	CONSTRAINT PK_RemindersTable_ServiceId_GrainId_ReminderName PRIMARY KEY(ServiceId, GrainId, ReminderName)
);

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
		GrainHash,
		Version
	)
	SELECT
		@ServiceId,
		@GrainId,
		@ReminderName,
		@StartTime,
		@Period,
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
