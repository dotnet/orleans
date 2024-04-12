/*
Orleans Streaming Message Sequence.
This sequence reduces contention on generation of [MessageId] values vs an identity column.
The CACHE parameter can be increased to further reduce contention.
*/
IF OBJECT_ID('[OrleansStreamSequence]') IS NULL
CREATE SEQUENCE [OrleansStreamSequence]
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

1. The table will see inserts only at the tail, as new messages are added.
2. The table will be polled with high frequency to reserve the first batch of rows that matches a well-known criteria ("visible" and "not expired" and "under max attempts").
3. The table will see rows being removed at the head as messages are confirmed.
4. The table will see rows being removed at the head as expired messages are moved to dead letters.
5. Due to the above queries locking more than one row, there is a possibility of deadlocks.
6. A few faulted or poisoned messages can linger for some time at the head before being moved to dead letters.
7. The table will occasionaly become empty as the cluster succeeds to catch up to all messages.

While [1-6] all cause page fragmentation over time, [7] self resolves this degradation by allowing sql server to eventually remove all pages.
Therefore the design attempts to optimise for [2] while assuming the resulting degradation eventually resolves itself.

The design also attempts to minimize the possibility of deadlocks at the expense of higher locking contention.
This happens by forcing all queries to select data in the exact same order of the clustered index and using eager row locking.

*/
IF OBJECT_ID('[OrleansStreamMessage]') IS NULL
CREATE TABLE [OrleansStreamMessage]
(
	/* Identifies the application */
	[ServiceId] NVARCHAR(150) NOT NULL,

	/* Identifies the individual queue */
	[QueueName] NVARCHAR(150) NOT NULL,

	/* The ascending number of the queue message */
	[MessageId] BIGINT NOT NULL,

	/* The number of times the message was dequeued */
	[Dequeued] INT NOT NULL,

	/* The UTC time at which the message will become visible */
	[VisibleOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the message will expire */
	[ExpiresOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the message was created - troubleshooting only */
	[CreatedOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the message was updated - troubleshooting only */
	[ModifiedOn] DATETIME2(7) NOT NULL,

	/* The arbitrarily large payload of the message */
	[Payload] VARBINARY(MAX) NULL,

	/* This Clustered PK supports the various scanning queries. */
	CONSTRAINT [PK_OrleansStreamMessage] PRIMARY KEY CLUSTERED
	(
		[ServiceId] ASC,
		[QueueName] ASC,
		[MessageId] ASC
	)
);
GO

/*
Orleans Streaming Dead Letters.

This table holds messages that could not be processed within the allowed number of attempts or that have expired.
*/
IF OBJECT_ID('[OrleansStreamDeadLetter]') IS NULL
CREATE TABLE [OrleansStreamDeadLetter]
(
	/* Identifies the application */
	[ServiceId] NVARCHAR(150) NOT NULL,

	/* Identifies the individual queue */
	[QueueName] NVARCHAR(150) NOT NULL,

	/* The ascending number of the queue message */
	[MessageId] BIGINT NOT NULL,

	/* The number of times the message was dequeued */
	[Dequeued] INT NOT NULL,

	/* The UTC time at which the message will become visible */
	[VisibleOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the message will expire */
	[ExpiresOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the message was created - troubleshooting only */
	[CreatedOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the message was updated - troubleshooting only */
	[ModifiedOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the message was given up on - troubleshooting only */
	[DeadOn] DATETIME2(7) NOT NULL,

	/* The UTC time at which the message is scheduled to be removed from dead letters */
	[RemoveOn] DATETIME2(7) NOT NULL,

	/* The arbitrarily large payload of the message */
	[Payload] VARBINARY(MAX) NULL,

	/* This Clustered PK supports the various scanning queries. */
	CONSTRAINT [PK_OrleansStreamDeadLetter] PRIMARY KEY CLUSTERED
	(
		[ServiceId] ASC,
		[QueueName] ASC,
		[MessageId] ASC
	)
);
GO

/* Enqueues an Orleans Streaming Message to the Messages table. */
CREATE PROCEDURE [EnqueueOrleansStreamMessage]
	@ServiceId NVARCHAR(150),
	@QueueName NVARCHAR(150),
	@Payload VARBINARY(MAX),
	@ExpiryTimeout INT
AS
BEGIN

SET NOCOUNT ON;

DECLARE @MessageId BIGINT = NEXT VALUE FOR [OrleansStreamSequence];
DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @ExpiresOn DATETIME2(7) = DATEADD(SECOND, @ExpiryTimeout, @Now);

INSERT INTO [OrleansStreamMessage]
(
	ServiceId,
	QueueName,
	MessageId,
	Dequeued,
	VisibleOn,
	ExpiresOn,
	CreatedOn,
	ModifiedOn,
	Payload
)
VALUES
(
	@ServiceId,
	@QueueName,
	@MessageId,
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
	'EXECUTE [EnqueueOrleansStreamMessage] @ServiceId = @ServiceId, @QueueName = @QueueName, @Payload = @Payload, @ExpiryTimeout = @ExpiryTimeout'
GO

/* Dequeues a message batch from the messages table. */
CREATE PROCEDURE [DequeueOrleansStreamMessages]
	@ServiceId NVARCHAR(150),
	@QueueName NVARCHAR(150),
	@BatchSize INT,
	@MaxAttempts INT,
	@VisibilityTimeout INT
AS
BEGIN

SET NOCOUNT ON;

DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @VisibleOn DATETIME2(7) = DATEADD(SECOND, @VisibilityTimeout, @Now);

WITH Batch AS
(
	SELECT TOP (@BatchSize)
		[ServiceId],
		[QueueName],
		[MessageId],
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
		AND [QueueName] = @QueueName
		AND [Dequeued] < @MaxAttempts
		AND [VisibleOn] <= @Now
		AND [ExpiresOn] > @Now
	ORDER BY
		[MessageId]
)
UPDATE Batch
SET
	[Dequeued] += 1,
	[VisibleOn] = @VisibleOn,
	[ModifiedOn] = @Now
OUTPUT
	[Inserted].[ServiceId],
	[Inserted].[QueueName],
	[Inserted].[MessageId],
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
	'EXECUTE [DequeueOrleansStreamMessages] @ServiceId = @ServiceId, @QueueName = @QueueName, @BatchSize = @BatchSize, @MaxAttempts = @MaxAttempts, @VisibilityTimeout = @VisibilityTimeout'
GO

/* Acknowledges delivery of a stream message. */
CREATE PROCEDURE [AckOrleansStreamMessage]
	@ServiceId NVARCHAR(150),
	@QueueName NVARCHAR(150),
	@MessageId BIGINT
AS
BEGIN

SET NOCOUNT ON;

DELETE FROM [OrleansStreamMessage]
WHERE
	[ServiceId] = @ServiceId
	AND [QueueName] = @QueueName
	AND [MessageId] = @MessageId

END
GO

INSERT INTO [OrleansQuery]
(
	[QueryKey],
	[QueryText]
)
SELECT
	'AcknowledgeStreamMessageKey',
	'EXECUTE [AckOrleansStreamMessage] @ServiceId = @ServiceId, @QueueName = @QueueName, @MessageId = @MessageId'
GO

/* Moves non-delivered messages from the message table to the dead letter table. */
CREATE PROCEDURE [CollectOrleansStreamMessages]
	@ServiceId NVARCHAR(150),
	@QueueName NVARCHAR(150),
	@BatchSize INT,
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
	SELECT TOP (@BatchSize)
		[ServiceId],
		[QueueName],
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
		AND [QueueName] = @QueueName
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
	[Deleted].[QueueName],
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
	[QueueName],
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
	'CollectStreamMessagesKey',
	'EXECUTE [CollectOrleansStreamMessages] @ServiceId = @ServiceId, @QueueName = @QueueName, @BatchSize = @BatchSize, @MaxAttempts = @MaxAttempts, @RemovalTimeout = @RemovalTimeout'
GO

/* Cleans up messages from the dead letters table. */
CREATE PROCEDURE [CollectOrleansStreamDeadLetters]
	@ServiceId NVARCHAR(150),
	@QueueName NVARCHAR(150),
	@BatchSize INT
AS
BEGIN

SET NOCOUNT ON;

DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

DELETE TOP (@BatchSize)
FROM [OrleansStreamDeadLetter]
WHERE
	[ServiceId] = @ServiceId
	AND [QueueName] = @QueueName
	AND [RemoveOn] <= @Now;

END
GO

INSERT INTO [OrleansQuery]
(
	[QueryKey],
	[QueryText]
)
SELECT
	'CollectStreamDeadLettersKey',
	'EXECUTE [CollectOrleansStreamDeadLetters] @ServiceId = @ServiceId, @QueueName = @QueueName, @BatchSize = @BatchSize'
GO
