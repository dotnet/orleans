/*
Orleans Stream Message Sequence.
This sequence reduces contention on generation of [MessageId] values vs an identity column.
The CACHE parameter can be increased to further reduce contention.
*/
CREATE SEQUENCE [OrleansStreamMessageSequence]
AS BIGINT
START WITH 1
INCREMENT BY 1
NO MAXVALUE
NO CYCLE
CACHE 1000;
GO

/*
Orleans Streaming Message Queue.

This table stores queued messages awaiting processing by Orleans.

The demands for this table are as follows:

1. The table will see inserts only at the tail, as new rows are added.
2. The table will be polled with high frequency to reserve the first batch of rows that matches a well-known criteria ("visible" and "not expired" and "under max attempts").
3. The table will see rows being removed at the head as messages are confirmed.
4. The table will see rows being removed at the head as expired messages are moved to dead letters.
5. Due to the above queries touching more than one row at a time, there is a possibility of deadlocks.
6. A few faulted or poisoned messages can linger for some time at the head before being moved to dead letters.
7. The table will occasionaly become empty or at least sparse as the cluster succeeds to catch up to all messages.

While [1-6] all cause page fragmentation over time, [7] self resolves this degradation by allowing sql server to eventually remove all pages.
Therefore the design attempts to optimise for [2] while assuming the resulting degradation eventually resolves itself.

The design also attempts to minimize the possibility of deadlocks at the expense of higher locking contention.
This happens by forcing all queries to touch data in the exact same order of the clustered index and using eager row locking as opposed to allowing upgrades.

*/
CREATE TABLE [OrleansStreamMessage]
(
	/* Identifies the application */
	[ServiceId] NVARCHAR(150) NOT NULL,

    /* Identifies the provider within the application */
    [ProviderId] NVARCHAR(150) NOT NULL,

	/* Identifies the individual queue shard as configured in the provider*/
	[QueueId] INT NOT NULL,

	/* The unique ascending number of the queued message */
	[MessageId] BIGINT NOT NULL,

    /* The confirmation receipt of the message */
    [Receipt] UNIQUEIDENTIFIER NOT NULL,

	/* The number of times the event was dequeued */
	[Dequeued] INT NOT NULL,

	/* The UTC time at which the event will become visible */
	[VisibleOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the event will expire */
	[ExpiresOn] DATETIME2(7) NOT NULL,

    /* The UTC time at which the event was created - troubleshooting only */
	[CreatedOn] DATETIME2(7) NOT NULL,

    /* The UTC time at which the event was updated - troubleshooting only */
	[ModifiedOn] DATETIME2(7) NOT NULL,

	/* The arbitrarily large payload of the event */
	[Payload] VARBINARY(MAX) NULL,

	/* This Clustered PK supports the various ordered scanning queries. */
    /* Its main purpose is to help partition the update row locks as to minimize dequeing contention. */
	CONSTRAINT [PK_OrleansStreamMessage] PRIMARY KEY CLUSTERED
	(
		[ServiceId] ASC,
        [ProviderId] ASC,
		[QueueId] ASC,
		[MessageId] ASC
	)
);
GO

/*
Orleans Streaming Dead Letters.

This table holds events that could not be processed within the allowed number of attempts or that have expired.
*/
CREATE TABLE [OrleansStreamDeadLetter]
(
	/* Identifies the application */
	[ServiceId] NVARCHAR(150) NOT NULL,

    /* Identifies the provider within the application */
    [ProviderId] NVARCHAR(150) NOT NULL,

	/* Identifies the individual queue shard as configured in the provider*/
	[QueueId] INT NOT NULL,

	/* The unique ascending number of the queued message */
	[MessageId] BIGINT NOT NULL,

    /* The confirmation receipt of the message */
    [Receipt] UNIQUEIDENTIFIER NOT NULL,

	/* The number of times the event was dequeued */
	[Dequeued] INT NOT NULL,

	/* The UTC time at which the event will become visible */
	[VisibleOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the event will expire */
	[ExpiresOn] DATETIME2(7) NOT NULL,

    /* The UTC time at which the event was created - troubleshooting only */
	[CreatedOn] DATETIME2(7) NOT NULL,

    /* The UTC time at which the event was updated - troubleshooting only */
	[ModifiedOn] DATETIME2(7) NOT NULL,

    /* The UTC time at which the event was given up on - troubleshooting only */
	[DeadOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the event is scheduled to be removed from dead letters */
	[RemoveOn] DATETIME2(7) NOT NULL,

	/* The arbitrarily large payload of the event */
	[Payload] VARBINARY(MAX) NULL,

	/* This Clustered PK supports the various ordered scanning queries. */
    /* Its main purpose is to help partition the update row locks as to minimize dequeing contention. */
	CONSTRAINT [PK_OrleansStreamDeadLetter] PRIMARY KEY CLUSTERED
	(
		[ServiceId] ASC,
        [ProviderId] ASC,
		[QueueId] ASC,
		[MessageId] ASC
	)
);
GO

/* Enqueues a message to the Orleans Streaming Message Queue */
CREATE PROCEDURE [EnqueueStreamMessage]
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId INT,
	@Payload VARBINARY(MAX),
	@ExpiryTimeout INT
AS
BEGIN

SET NOCOUNT ON;

DECLARE @MessageId BIGINT = NEXT VALUE FOR [OrleansStreamMessageSequence];
DECLARE @Receipt UNIQUEIDENTIFIER = CAST(0x0 AS UNIQUEIDENTIFIER);
DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @ExpiresOn DATETIME2(7) = DATEADD(SECOND, @ExpiryTimeout, @Now);

INSERT INTO [OrleansStreamMessage]
(
	ServiceId,
    ProviderId,
	QueueId,
	MessageId,
    Receipt,
	Dequeued,
	VisibleOn,
	ExpiresOn,
	CreatedOn,
	ModifiedOn,
	Payload
)
OUTPUT
    [Inserted].[ServiceId],
    [Inserted].[ProviderId],
    [Inserted].[QueueId],
    [Inserted].[MessageId]
VALUES
(
	@ServiceId,
    @ProviderId,
	@QueueId,
	@MessageId,
    @Receipt,
	0,
	@Now,
	@ExpiresOn,
	@Now,
	@Now,
	@Payload
);

END
GO

INSERT INTO [OrleansQuery]
(
	[QueryKey],
	[QueryText]
)
SELECT
	'EnqueueStreamMessageKey',
	'EXECUTE [EnqueueStreamMessage] @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @Payload = @Payload, @ExpiryTimeout = @ExpiryTimeout'
GO

/* Dequeues message batches from the Orleans Streaming Message Queue */
CREATE PROCEDURE [DequeueStreamMessages]
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId INT,
    @MaxCount INT,
	@MaxAttempts INT,
	@VisibilityTimeout INT
AS
BEGIN

SET NOCOUNT ON;

DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @VisibleOn DATETIME2(7) = DATEADD(SECOND, @VisibilityTimeout, @Now);

WITH Batch AS
(
	SELECT TOP (@MaxCount)
		[ServiceId],
        [ProviderId],
		[QueueId],
		[MessageId],
        [Receipt],
		[Dequeued],
		[VisibleOn],
		[ExpiresOn],
		[CreatedOn],
		[ModifiedOn],
		[Payload]
	FROM
		[OrleansStreamMessage] WITH (ROWLOCK, UPDLOCK, HOLDLOCK)
	WHERE
		[ServiceId] = @ServiceId
        AND [ProviderId] = @ProviderId
		AND [QueueId] = @QueueId
		AND [Dequeued] < @MaxAttempts
		AND [VisibleOn] <= @Now
		AND [ExpiresOn] > @Now
	ORDER BY
		[MessageId]
)
UPDATE Batch
SET
	[Dequeued] += 1,
    [Receipt] = NEWID(),
	[VisibleOn] = @VisibleOn,
	[ModifiedOn] = @Now
OUTPUT
	[Inserted].[ServiceId],
    [Inserted].[ProviderId],
	[Inserted].[QueueId],
	[Inserted].[MessageId],
    [Inserted].[Receipt],
	[Inserted].[Dequeued],
	[Inserted].[VisibleOn],
	[Inserted].[ExpiresOn],
	[Inserted].[CreatedOn],
	[Inserted].[ModifiedOn],
	[Inserted].[Payload]
FROM
	Batch

END
GO

INSERT INTO [OrleansQuery]
(
	[QueryKey],
	[QueryText]
)
SELECT
	'DequeueStreamMessagesKey',
	'EXECUTE [DequeueStreamMessages] @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @MaxCount = @MaxCount, @MaxAttempts = @MaxAttempts, @VisibilityTimeout = @VisibilityTimeout'
GO

/* Confirms delivery of a stream message. */
CREATE PROCEDURE [ConfirmStreamMessages]
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId INT,
    @Items NVARCHAR(MAX)
AS
BEGIN

SET NOCOUNT ON;

DECLARE @ItemsTable TABLE
(
    [MessageId] BIGINT NOT NULL,
    [Receipt] UNIQUEIDENTIFIER NOT NULL
);

WITH Items AS
(
	SELECT [Value] FROM STRING_SPLIT(@Items, '|')
)
INSERT INTO @ItemsTable
(
    [MessageId],
    [Receipt]
)
SELECT
	CAST(SUBSTRING([Value], 1, CHARINDEX(':', [Value], 1) - 1) AS INT) AS [MessageId],
	CAST(SUBSTRING([Value], CHARINDEX(':', [Value], 1) + 1, LEN([Value])) AS UNIQUEIDENTIFIER) AS [Receipt]
FROM
	Items;

DELETE FROM [OrleansStreamMessage]
OUTPUT
    [Deleted].[ServiceId],
    [Deleted].[ProviderId],
    [Deleted].[QueueId],
    [Deleted].[MessageId]
FROM
    [OrleansStreamMessage] AS [M]
    INNER JOIN @ItemsTable AS [I]
        ON [M].[MessageId] = [I].[MessageId]
        AND [M].[Receipt] = [I].[Receipt]
WHERE
	[ServiceId] = @ServiceId
    AND [ProviderId] = @ProviderId
	AND [QueueId] = @QueueId

END
GO

INSERT INTO [OrleansQuery]
(
	[QueryKey],
	[QueryText]
)
SELECT
	'ConfirmStreamMessagesKey',
	'EXECUTE [ConfirmStreamMessages] @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @Items = @Items'
GO

/* Moves non-delivered messages from the message table to the dead letter table for human troubleshooting. */
CREATE PROCEDURE [CleanStreamMessages]
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId INT,
	@MaxCount INT,
	@MaxAttempts INT,
	@RemovalTimeout INT
AS
BEGIN

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @RemoveOn DATETIME2(7) = DATEADD(SECOND, @RemovalTimeout, @Now);

WITH Batch AS
(
	SELECT TOP (@MaxCount)
		[ServiceId],
        [ProviderId],
		[QueueId],
		[MessageId],
		[Dequeued],
		[VisibleOn],
		[ExpiresOn],
		[CreatedOn],
		[ModifiedOn],
		[DeadOn] = @Now,
		[RemoveOn] = @RemoveOn,
		[Payload]
	FROM
		[OrleansStreamMessage] WITH (ROWLOCK, UPDLOCK, HOLDLOCK)
	WHERE
		[ServiceId] = @ServiceId
        AND [ProviderId] = @ProviderId
		AND [QueueId] = @QueueId
		AND
		(
			-- a message is no longer dequeueable if the last attempt timed out
			([Dequeued] >= @MaxAttempts AND [VisibleOn] <= @Now)
			OR
			-- a message is no longer dequeueable if it has expired regardless
			([ExpiresOn] <= @Now)
		)
	ORDER BY
		[MessageId]
)
DELETE FROM Batch
OUTPUT
	[Deleted].[ServiceId],
    [Deleted].[ProviderId],
	[Deleted].[QueueId],
	[Deleted].[MessageId],
	[Deleted].[Dequeued],
	[Deleted].[VisibleOn],
	[Deleted].[ExpiresOn],
	[Deleted].[CreatedOn],
	[Deleted].[ModifiedOn],
	[Deleted].[DeadOn],
	[Deleted].[RemoveOn],
	[Deleted].[Payload]
INTO [OrleansStreamDeadLetter]
(
	[ServiceId],
    [ProviderId],
	[QueueId],
	[MessageId],
	[Dequeued],
	[VisibleOn],
	[ExpiresOn],
	[CreatedOn],
	[ModifiedOn],
	[DeadOn],
	[RemoveOn],
	[Payload]
);

END
GO

INSERT INTO [OrleansQuery]
(
	[QueryKey],
	[QueryText]
)
SELECT
	'CleanStreamMessagesKey',
	'EXECUTE [CleanStreamMessages] @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @MaxCount = @MaxCount, @MaxAttempts = @MaxAttempts, @RemovalTimeout = @RemovalTimeout'
GO

/* Removes messages from the dead letters table. */
CREATE PROCEDURE [CleanDeadLetters]
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId INT,
	@MaxCount INT
AS
BEGIN

SET NOCOUNT ON;

DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

WITH Batch AS
(
    SELECT TOP (@MaxCount)
        [ServiceId],
        [ProviderId],
        [QueueId],
        [MessageId]
    FROM
        [OrleansStreamDeadLetter] WITH (ROWLOCK, XLOCK, HOLDLOCK)
    WHERE
        [ServiceId] = @ServiceId
        AND [ProviderId] = @ProviderId
        AND [QueueId] = @QueueId
    ORDER BY
        [MessageId]
)
DELETE FROM Batch;

END
GO

INSERT INTO [OrleansQuery]
(
	[QueryKey],
	[QueryText]
)
SELECT
	'CleanDeadLettersKey',
	'EXECUTE [CleanDeadLetters] @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @MaxCount = @MaxCount'
GO
