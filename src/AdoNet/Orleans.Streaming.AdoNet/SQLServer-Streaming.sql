/*
Orleans Stream Message Sequence.
This sequence reduces contention on generation of [MessageId] values vs an identity column.
The CACHE parameter can be increased to further reduce contention.
*/
CREATE SEQUENCE OrleansStreamMessageSequence
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
This happens by forcing all queries to touch data in the exact same order of the clustered index.
This induces ordered resource lock acquisition while avoiding the cost of ordering itself.

*/
CREATE TABLE OrleansStreamMessage
(
	/* Identifies the application */
	ServiceId NVARCHAR(150) NOT NULL,

    /* Identifies the provider within the application */
    ProviderId NVARCHAR(150) NOT NULL,

	/* Identifies the individual queue shard as configured in the provider*/
	QueueId NVARCHAR(150) NOT NULL,

	/* The unique ascending number of the queued message */
	MessageId BIGINT NOT NULL,

	/* The number of times the event was dequeued */
	Dequeued INT NOT NULL,

	/* The UTC time at which the event will become visible */
	VisibleOn DATETIME2(7) NOT NULL,

	/* The UTC time at which the event will expire */
	ExpiresOn DATETIME2(7) NOT NULL,

    /* The UTC time at which the event was created - troubleshooting only */
	CreatedOn DATETIME2(7) NOT NULL,

    /* The UTC time at which the event was updated - troubleshooting only */
	ModifiedOn DATETIME2(7) NOT NULL,

	/* The arbitrarily large payload of the event */
	Payload VARBINARY(MAX) NOT NULL,

	/* This Clustered PK supports the various ordered scanning queries. */
	CONSTRAINT PK_OrleansStreamMessage PRIMARY KEY CLUSTERED
	(
		ServiceId ASC,
        ProviderId ASC,
		QueueId ASC,
		MessageId ASC
	)
);
GO

/*
Orleans Streaming Dead Letters.

This table holds events that could not be processed within the allowed number of attempts or that have expired.
*/
CREATE TABLE OrleansStreamDeadLetter
(
	/* Identifies the application */
	ServiceId NVARCHAR(150) NOT NULL,

    /* Identifies the provider within the application */
    ProviderId NVARCHAR(150) NOT NULL,

	/* Identifies the individual queue shard as configured in the provider*/
	QueueId NVARCHAR(150) NOT NULL,

	/* The unique ascending number of the queued message */
	MessageId BIGINT NOT NULL,

	/* The number of times the event was dequeued */
	Dequeued INT NOT NULL,

	/* The UTC time at which the event will become visible */
	VisibleOn DATETIME2(7) NOT NULL,

	/* The UTC time at which the event will expire */
	ExpiresOn DATETIME2(7) NOT NULL,

    /* The UTC time at which the event was created - troubleshooting only */
	CreatedOn DATETIME2(7) NOT NULL,

    /* The UTC time at which the event was updated - troubleshooting only */
	ModifiedOn DATETIME2(7) NOT NULL,

    /* The UTC time at which the event was given up on - troubleshooting only */
	DeadOn DATETIME2(7) NOT NULL,

	/* The UTC time at which the event is scheduled to be removed from dead letters */
	RemoveOn DATETIME2(7) NOT NULL,

	/* The arbitrarily large payload of the event */
	Payload VARBINARY(MAX) NULL,

	/* This Clustered PK supports the various ordered scanning queries. */
    /* Its main purpose is to help partition the update row locks as to minimize dequeing contention. */
	CONSTRAINT PK_OrleansStreamDeadLetter PRIMARY KEY CLUSTERED
	(
		ServiceId ASC,
        ProviderId ASC,
		QueueId ASC,
		MessageId ASC
	)
);
GO

/*
Orleans Streaming Control Table.
This table holds schedule variables to help providers self manage their own work.
*/
CREATE TABLE OrleansStreamControl
(
	/* Identifies the application */
	ServiceId NVARCHAR(150) NOT NULL,

    /* Identifies the provider within the application */
    ProviderId NVARCHAR(150) NOT NULL,

	/* Identifies the individual queue shard as configured in the provider */
	QueueId NVARCHAR(150) NOT NULL,

    /* The next due schedule for messages to be evicted */
    EvictOn DATETIME2(7) NOT NULL,

    /* Each row represents a flat configuration object for an individual queue */
	CONSTRAINT PK_OrleansStreamControl PRIMARY KEY CLUSTERED
	(
		ServiceId ASC,
        ProviderId ASC,
		QueueId ASC
	)
);
GO

/* Queues a message to the Orleans Streaming Message Queue */
CREATE PROCEDURE QueueStreamMessage
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId NVARCHAR(150),
	@Payload VARBINARY(MAX),
	@ExpiryTimeout INT
AS
BEGIN

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @MessageId BIGINT = NEXT VALUE FOR OrleansStreamMessageSequence;
DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @ExpiresOn DATETIME2(7) = DATEADD(SECOND, @ExpiryTimeout, @Now);

INSERT INTO OrleansStreamMessage
(
	ServiceId,
    ProviderId,
	QueueId,
	MessageId,
	Dequeued,
	VisibleOn,
	ExpiresOn,
	CreatedOn,
	ModifiedOn,
	Payload
)
OUTPUT
    Inserted.ServiceId,
    Inserted.ProviderId,
    Inserted.QueueId,
    Inserted.MessageId
VALUES
(
	@ServiceId,
    @ProviderId,
	@QueueId,
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

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'QueueStreamMessageKey',
	'EXECUTE QueueStreamMessage @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @Payload = @Payload, @ExpiryTimeout = @ExpiryTimeout'
GO

/* Gets message batches from the Orleans Streaming Message Queue */
/* Also opportunistically performs eviction activities when they are due */
CREATE PROCEDURE GetStreamMessages
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId NVARCHAR(150),
    @MaxCount INT,
	@MaxAttempts INT,
	@VisibilityTimeout INT,
    @RemovalTimeout INT,
    @EvictionInterval INT,
    @EvictionBatchSize INT
AS
BEGIN

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @VisibleOn DATETIME2(7) = DATEADD(SECOND, @VisibilityTimeout, @Now);

/* lightweight check to see if an eviction activity is due */
DECLARE @EvictOn DATETIME2(7) =
(
    SELECT EvictOn
    FROM OrleansStreamControl
    WHERE
        ServiceId = @ServiceId
        AND ProviderId = @ProviderId
        AND QueueId = @QueueId
);

/* escalate to a eviction attempt only if an activity is due */
IF @EvictOn IS NULL OR @EvictOn < @Now
BEGIN

    /* attempt to win a race to update the schedule */
    /* this will also initialize the table if necessary */
    WITH Candidate AS
    (
        SELECT
            ServiceId = @ServiceId,
            ProviderId = @ProviderId,
            QueueId = @QueueId,
            Now = @Now,
            EvictOn = DATEADD(SECOND, @EvictionInterval, @Now)
    )
    MERGE OrleansStreamControl WITH (UPDLOCK, HOLDLOCK) AS T
    USING Candidate AS S
    ON T.ServiceId = S.ServiceId
    AND T.ProviderId = S.ProviderId
    AND T.QueueId = S.QueueId
    WHEN MATCHED AND T.EvictOn < S.Now THEN
    UPDATE SET T.EvictOn = S.EvictOn
    WHEN NOT MATCHED BY TARGET THEN
    INSERT
    (
        ServiceId,
        ProviderId,
        QueueId,
        EvictOn
    )
    VALUES
    (
        ServiceId,
        ProviderId,
        QueueId,
        EvictOn
    );

    /* if the above statement won the race then we also get to run the eviction */
    /* other concurrent queries will continue running as normal until the next due time */
    IF (@@ROWCOUNT > 0)
    BEGIN

        /* evict messages */
        EXECUTE EvictStreamMessages
            @ServiceId = @ServiceId,
            @ProviderId = @ProviderId,
            @QueueId = @QueueId,
            @MaxAttempts = @MaxAttempts,
            @RemovalTimeout = @RemovalTimeout,
            @BatchSize = @EvictionBatchSize

        /* evict dead letters */
        EXECUTE EvictStreamDeadLetters
            @ServiceId = @ServiceId,
            @ProviderId = @ProviderId,
            @QueueId = @QueueId,
            @BatchSize = @EvictionBatchSize;
            
    END;

END;

/* update messages in the exact same order as the clustered index to avoid deadlocks with other queries */
WITH Batch AS
(
	SELECT TOP (@MaxCount)
		ServiceId,
        ProviderId,
		QueueId,
		MessageId,
		Dequeued,
		VisibleOn,
		ExpiresOn,
		CreatedOn,
		ModifiedOn,
		Payload
	FROM
		OrleansStreamMessage WITH (UPDLOCK)
	WHERE
		ServiceId = @ServiceId
        AND ProviderId = @ProviderId
		AND QueueId = @QueueId
		AND Dequeued < @MaxAttempts
		AND VisibleOn <= @Now
		AND ExpiresOn > @Now
	ORDER BY
        ServiceId,
        ProviderId,
        QueueId,
		MessageId
)
UPDATE Batch
SET
	Dequeued += 1,
	VisibleOn = @VisibleOn,
	ModifiedOn = @Now
OUTPUT
	Inserted.ServiceId,
    Inserted.ProviderId,
	Inserted.QueueId,
	Inserted.MessageId,
	Inserted.Dequeued,
	Inserted.VisibleOn,
	Inserted.ExpiresOn,
	Inserted.CreatedOn,
	Inserted.ModifiedOn,
	Inserted.Payload
FROM
	Batch;

END
GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'GetStreamMessagesKey',
	'EXECUTE GetStreamMessages @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @MaxCount = @MaxCount, @MaxAttempts = @MaxAttempts, @VisibilityTimeout = @VisibilityTimeout, @RemovalTimeout = @RemovalTimeout, @EvictionInterval = @EvictionInterval, @EvictionBatchSize = @EvictionBatchSize'
GO

/* Confirms delivery of a stream message. */
CREATE PROCEDURE ConfirmStreamMessages
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId NVARCHAR(150),
    @Items NVARCHAR(MAX)
AS
BEGIN

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* parse the message identifiers to be deleted */
DECLARE @ItemsTable TABLE
(
    MessageId BIGINT PRIMARY KEY NOT NULL,
    Dequeued INT NOT NULL
);
WITH Items AS
(
	SELECT Value FROM STRING_SPLIT(@Items, '|')
)
INSERT INTO @ItemsTable
(
    MessageId,
    Dequeued
)
SELECT
	CAST(SUBSTRING(Value, 1, CHARINDEX(':', Value, 1) - 1) AS BIGINT) AS MessageId,
	CAST(SUBSTRING(Value, CHARINDEX(':', Value, 1) + 1, LEN(Value)) AS INT) AS Dequeued
FROM
	Items;

/* count the number of messages to delete so we can use order by in the next query */
DECLARE @Count INT = (SELECT COUNT(*) FROM @ItemsTable);

/* delete messages in the exact same order as the clustered index to avoid deadlocks with other queries */
WITH Batch AS
(
	SELECT TOP (@Count)
		*
	FROM
		OrleansStreamMessage AS M WITH (UPDLOCK, HOLDLOCK)
	WHERE
		ServiceId = @ServiceId
        AND ProviderId = @ProviderId
		AND QueueId = @QueueId
        AND EXISTS
        (
            SELECT *
            FROM @ItemsTable AS I
            WHERE I.MessageId = M.MessageId
            AND I.Dequeued = M.Dequeued
        )
	ORDER BY
        ServiceId,
        ProviderId,
        QueueId,
		MessageId
)
DELETE FROM Batch
OUTPUT
    Deleted.ServiceId,
    Deleted.ProviderId,
    Deleted.QueueId,
    Deleted.MessageId;

END
GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'ConfirmStreamMessagesKey',
	'EXECUTE ConfirmStreamMessages @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @Items = @Items'
GO

/* Applies delivery failure rules to the specified message. */
/* If the message has been dequeued too many times, we move it to the dead letter table. */
/* If the message has expired, we move to the dead letter table. */
/* If the message is still eligible for delivery, it is made visible again. */
CREATE PROCEDURE FailStreamMessage
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId NVARCHAR(150),
    @MessageId BIGINT,
	@MaxAttempts INT,
	@RemovalTimeout INT
AS
BEGIN

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @RemoveOn DATETIME2(7) = DATEADD(SECOND, @RemovalTimeout, @Now);

/* if the message can still be dequeued then attempt to mark it visible again */
UPDATE OrleansStreamMessage
SET
    VisibleOn = @Now,
    ModifiedOn = @Now
WHERE
    ServiceId = @ServiceId
    AND ProviderId = @ProviderId
    AND QueueId = @QueueId
    AND MessageId = @MessageId
    AND Dequeued < @MaxAttempts;

IF @@ROWCOUNT > 0 RETURN;

/* otherwise attempt to move the message to dead letters */
DELETE FROM OrleansStreamMessage
OUTPUT
    Deleted.ServiceId,
    Deleted.ProviderId,
    Deleted.QueueId,
    Deleted.MessageId,
    Deleted.Dequeued,
    Deleted.VisibleOn,
    Deleted.ExpiresOn,
    Deleted.CreatedOn,
    Deleted.ModifiedOn,
    @Now AS DeadOn,
    @RemoveOn AS RemoveOn,
    Deleted.Payload
INTO OrleansStreamDeadLetter
(
    ServiceId,
    ProviderId,
    QueueId,
    MessageId,
    Dequeued,
    VisibleOn,
    ExpiresOn,
    CreatedOn,
    ModifiedOn,
    DeadOn,
    RemoveOn,
    Payload
)
WHERE
    ServiceId = @ServiceId
    AND ProviderId = @ProviderId
    AND QueueId = @QueueId
    AND MessageId = @MessageId;

END
GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'FailStreamMessageKey',
	'EXECUTE FailStreamMessage @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @MessageId = @MessageId, @MaxAttempts = @MaxAttempts, @RemovalTimeout = @RemovalTimeout'
GO

/* Moves non-delivered messages from the message table to the dead letter table for human troubleshooting. */
CREATE PROCEDURE EvictStreamMessages
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId NVARCHAR(150),
	@BatchSize INT,
	@MaxAttempts INT,
	@RemovalTimeout INT
AS
BEGIN

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @RemoveOn DATETIME2(7) = DATEADD(SECOND, @RemovalTimeout, @Now);

/* delete messages in the exact same order as the clustered index to avoid deadlocks with other queries */
WITH Batch AS
(
	SELECT TOP (@BatchSize)
		ServiceId,
        ProviderId,
		QueueId,
		MessageId,
		Dequeued,
		VisibleOn,
		ExpiresOn,
		CreatedOn,
		ModifiedOn,
		DeadOn = @Now,
		RemoveOn = @RemoveOn,
		Payload
	FROM
		OrleansStreamMessage WITH (UPDLOCK)
	WHERE
		ServiceId = @ServiceId
        AND ProviderId = @ProviderId
		AND QueueId = @QueueId

        -- the message was given the opportunity to complete
        AND VisibleOn <= @Now
		AND
		(
			-- the message was dequeued too many times
			Dequeued >= @MaxAttempts
			OR
			-- the message expired
			ExpiresOn <= @Now
		)
	ORDER BY
        ServiceId,
        ProviderId,
        QueueId,
		MessageId
)
DELETE FROM Batch
OUTPUT
	Deleted.ServiceId,
    Deleted.ProviderId,
	Deleted.QueueId,
	Deleted.MessageId,
	Deleted.Dequeued,
	Deleted.VisibleOn,
	Deleted.ExpiresOn,
	Deleted.CreatedOn,
	Deleted.ModifiedOn,
	Deleted.DeadOn,
	Deleted.RemoveOn,
	Deleted.Payload
INTO OrleansStreamDeadLetter
(
	ServiceId,
    ProviderId,
	QueueId,
	MessageId,
	Dequeued,
	VisibleOn,
	ExpiresOn,
	CreatedOn,
	ModifiedOn,
	DeadOn,
	RemoveOn,
	Payload
);

END
GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'EvictStreamMessagesKey',
	'EXECUTE EvictStreamMessages @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @BatchSize = @BatchSize, @MaxAttempts = @MaxAttempts, @RemovalTimeout = @RemovalTimeout'
GO

/* Removes messages from the dead letters table. */
CREATE PROCEDURE EvictStreamDeadLetters
	@ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
	@QueueId NVARCHAR(150),
	@BatchSize INT
AS
BEGIN

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

/* delete messages in the exact same order as the clustered index to avoid deadlocks with other queries */
WITH Batch AS
(
    SELECT TOP (@BatchSize)
        ServiceId,
        ProviderId,
        QueueId,
        MessageId
    FROM
        OrleansStreamDeadLetter WITH (UPDLOCK)
    WHERE
        ServiceId = @ServiceId
        AND ProviderId = @ProviderId
        AND QueueId = @QueueId
        AND RemoveOn <= @Now
    ORDER BY
        ServiceId,
        ProviderId,
        QueueId,
        MessageId
)
DELETE FROM Batch;

END
GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'EvictStreamDeadLettersKey',
	'EXECUTE EvictStreamDeadLetters @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @BatchSize = @BatchSize'
GO
