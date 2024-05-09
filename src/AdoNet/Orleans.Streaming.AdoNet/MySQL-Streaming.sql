CREATE TABLE OrleansStreamMessageSequence
(
    MessageId BIGINT NOT NULL
);
INSERT INTO OrleansStreamMessageSequence
SELECT 0
WHERE NOT EXISTS (SELECT * FROM OrleansStreamMessageSequence);

DELIMITER $$

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
	VisibleOn DATETIME(6) NOT NULL,

	/* The UTC time at which the event will expire */
	ExpiresOn DATETIME(6) NOT NULL,

    /* The UTC time at which the event was created - troubleshooting only */
	CreatedOn DATETIME(6) NOT NULL,

    /* The UTC time at which the event was updated - troubleshooting only */
	ModifiedOn DATETIME(6) NOT NULL,

	/* The arbitrarily large payload of the event */
	Payload LONGBLOB NOT NULL,

	/* This PK supports the various ordered scanning queries. */
	PRIMARY KEY (ServiceId, ProviderId, QueueId, MessageId)
);

DELIMITER $$

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
	VisibleOn DATETIME(6) NOT NULL,

	/* The UTC time at which the event will expire */
	ExpiresOn DATETIME(6) NOT NULL,

    /* The UTC time at which the event was created - troubleshooting only */
	CreatedOn DATETIME(6) NOT NULL,

    /* The UTC time at which the event was updated - troubleshooting only */
	ModifiedOn DATETIME(6) NOT NULL,

    /* The UTC time at which the event was given up on - troubleshooting only */
	DeadOn DATETIME(6) NOT NULL,

	/* The UTC time at which the event is scheduled to be removed from dead letters */
	RemoveOn DATETIME(6) NOT NULL,

	/* The arbitrarily large payload of the event */
	Payload LONGBLOB NULL,

	/* This PK supports the various ordered scanning queries. */
	PRIMARY KEY (ServiceId, ProviderId, QueueId, MessageId)
);

DELIMITER $$

CREATE TABLE OrleansStreamControl
(
	/* Identifies the application */
	ServiceId NVARCHAR(150) NOT NULL,

    /* Identifies the provider within the application */
    ProviderId NVARCHAR(150) NOT NULL,

	/* Identifies the individual queue shard as configured in the provider */
	QueueId NVARCHAR(150) NOT NULL,

    /* The next due schedule for messages to be evicted */
    EvictOn DATETIME(6) NOT NULL,

    /* Each row represents a flat configuration object for an individual queue */
	PRIMARY KEY (ServiceId, ProviderId, QueueId)
);

DELIMITER $$

CREATE PROCEDURE QueueStreamMessage
(
    IN _ServiceId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _QueueId NVARCHAR(150),
    IN _Payload LONGBLOB,
    IN _ExpiryTimeout INT
)
BEGIN

DECLARE _MessageId BIGINT;
DECLARE _Now DATETIME(6);
DECLARE _ExpiresOn DATETIME(6);
DECLARE EXIT HANDLER FOR SQLEXCEPTION ROLLBACK;

SET _Now = UTC_TIMESTAMP(6);
SET _ExpiresOn = DATE_ADD(_Now, INTERVAL _ExpiryTimeout SECOND);

UPDATE OrleansStreamMessageSequence
SET MessageId = LAST_INSERT_ID(MessageId + 1);

SET _MessageId = LAST_INSERT_ID();

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
VALUES
(
    _ServiceId,
    _ProviderId,
    _QueueId,
    _MessageId,
    0,
    _Now,
    _ExpiresOn,
    _Now,
    _Now,
    _Payload
)
RETURNING
    ServiceId,
    ProviderId,
    QueueId,
    MessageId;

END;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'QueueStreamMessageKey',
	'CALL QueueStreamMessage(@ServiceId, @ProviderId, @QueueId, @Payload, @ExpiryTimeout)'

DELIMITER $$

CREATE PROCEDURE GetStreamMessages
(
    IN _ServiceId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
	IN _QueueId NVARCHAR(150),
    IN _MaxCount INT,
	IN _MaxAttempts INT,
	IN _VisibilityTimeout INT,
    IN _RemovalTimeout INT,
    IN _EvictionInterval INT,
    IN _EvictionBatchSize INT
)
BEGIN

DECLARE _Now DATETIME(6);
DECLARE _VisibleOn DATETIME(6);
DECLARE _EvictOn DATETIME(6);
DECLARE EXIT HANDLER FOR SQLEXCEPTION ROLLBACK;

SET _Now = UTC_TIMESTAMP(6);
SET _VisibleOn = DATE_ADD(_Now, INTERVAL _VisibilityTimeout SECOND);

SET _EvictOn =
(
    SELECT EvictOn
    FROM OrleansStreamControl
    WHERE
        ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
);

/* initialize the control row as necessary */
IF _EvictOn IS NULL THEN

    INSERT OrleansStreamControl
    (
        ServiceId,
        ProviderId,
        QueueId,
        EvictOn
    )
    SELECT
        _ServiceId,
        _ProviderId,
        _QueueId,
        _Now
    WHERE
        NOT EXISTS
        (
            SELECT *
            FROM OrleansStreamControl
            WHERE
                ServiceId = _ServiceId
                AND ProviderId = _ProviderId
                AND QueueId = _QueueId
        );

    SELECT EvictOn INTO _EvictOn
    FROM OrleansStreamControl
    WHERE
        ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId;

END IF;

IF _EvictOn < _Now THEN

    /* attempt to win a race to update the control row */
    UPDATE OrleansStreamControl
    SET EvictOn = DATE_ADD(_Now, INTERVAL _EvictionInterval SECOND)
    WHERE
        ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
        AND EvictOn < _EvictOn;

    /* if we won the race then we also run eviction */
    IF ROW_COUNT() > 0 THEN
        CALL EvictStreamMessages(_ServiceId, _ProviderId, _QueueId, _MaxAttempts, _RemovalTimeout, _EvictionBatchSize);
        CALL EvictStreamDeadLetters(_ServiceId, _ProviderId, _QueueId, _EvictionBatchSize);
    END IF;

END IF;

REPLACE OrleansStreamMessage
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
SELECT
    ServiceId,
    ProviderId,
    QueueId,
    MessageId,
    Dequeued + 1,
    _VisibleOn,
    ExpiresOn,
    CreatedOn,
    _Now,
    Payload
FROM
    OrleansStreamMessage
WHERE
    ServiceId = _ServiceId
    AND ProviderId = _ProviderId
    AND QueueId = _QueueId
    AND Dequeued < _MaxAttempts
    AND VisibleOn <= _Now
    AND ExpiresOn > _Now
ORDER BY
    ServiceId,
    ProviderId,
    QueueId,
    MessageId
LIMIT
    _MaxCount
RETURNING
    ServiceId,
    ProviderId,
    QueueId,
    MessageId,
    Dequeued,
    VisibleOn,
    ExpiresOn,
    CreatedOn,
    ModifiedOn,
    Payload;

END;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'GetStreamMessagesKey',
	'CALL GetStreamMessages(@ServiceId, @ProviderId, @QueueId, @MaxCount, @MaxAttempts, @VisibilityTimeout, @RemovalTimeout, @EvictionInterval, @EvictionBatchSize)';

DELIMITER $$

CREATE PROCEDURE ConfirmStreamMessages
(
    IN _ServiceId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _QueueId NVARCHAR(150),
    IN _Items LONGTEXT
)
BEGIN

DECLARE _Delimiter1 NVARCHAR(1);
DECLARE _Delimiter2 NVARCHAR(1);
DECLARE _Value LONGTEXT;
DECLARE _MessageId BIGINT;
DECLARE _Dequeued INT;
DECLARE _Count INT;
DECLARE EXIT HANDLER FOR SQLEXCEPTION ROLLBACK;

SET _Delimiter1 = '|';
SET _Delimiter2 = ':';
SET _Items = CONCAT(_Items, _Delimiter1);

/* parse the message identifiers to be deleted */
CREATE TEMPORARY TABLE _ItemsTable
(
    MessageId BIGINT PRIMARY KEY NOT NULL,
    Dequeued INT NOT NULL
);

WHILE LOCATE(_Delimiter1, _Items) > 0 DO

    SET _Value = SUBSTRING_INDEX(_Items, _Delimiter1, 1);
    SET _MessageId = CAST(SUBSTRING_INDEX(_Value, _Delimiter2, 1) AS UNSIGNED);
    SET _Dequeued = CAST(SUBSTRING_INDEX(_Value, _Delimiter2, -1) AS UNSIGNED);
    
    INSERT INTO _ItemsTable (MessageId, Dequeued)
    VALUES (_MessageId, _Dequeued);

    SET _Items = SUBSTRING(_Items, LOCATE(_Delimiter1, _Items) + 1);

END WHILE;

/* count the number of messages to delete so we can use order by in the next query */
SET _Count = (SELECT COUNT(*) FROM _ItemsTable);

/* delete messages in the exact same order as the clustered index to avoid deadlocks with other queries */
START TRANSACTION;

CREATE TEMPORARY TABLE _Batch
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    MessageId BIGINT NOT NULL,
    PRIMARY KEY (ServiceId, ProviderId, QueueId, MessageId)
);

INSERT INTO _Batch
SELECT
	*
FROM
	OrleansStreamMessage AS M
WHERE
	ServiceId = _ServiceId
    AND ProviderId = _ProviderId
	AND QueueId = _QueueId
    AND EXISTS
    (
        SELECT *
        FROM _ItemsTable AS I
        WHERE I.MessageId = M.MessageId
        AND I.Dequeued = M.Dequeued
    )
ORDER BY
    ServiceId,
    ProviderId,
    QueueId,
	MessageId
LIMIT _Count
FOR UPDATE;

DELETE FROM OrleansStreamMessage
WHERE EXISTS
(
    SELECT *
    FROM _Batch AS B
    WHERE
        B.ServiceId = OrleansStreamMessage.ServiceId
        AND B.ProviderId = OrleansStreamMessage.ProviderId
        AND B.QueueId = OrleansStreamMessage.QueueId
        AND B.MessageId = OrleansStreamMessage.MessageId
)
RETURNING
    ServiceId,
    ProviderId,
    QueueId,
    MessageId;

COMMIT;
END;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'ConfirmStreamMessagesKey',
	'CALL ConfirmStreamMessages(@ServiceId, @ProviderId, @QueueId, @Items)';

DELIMITER $$

CREATE PROCEDURE EvictStreamMessage
(
    IN _ServiceId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _QueueId NVARCHAR(150),
    IN _MessageId INT,
    IN _MaxAttempts INT,
    IN _RemovalTimeout INT
)
BEGIN

    DECLARE _Now DATETIME(6);
    DECLARE _RemoveOn DATETIME(6);
    DECLARE EXIT HANDLER FOR SQLEXCEPTION ROLLBACK;

    SET _Now = UTC_TIMESTAMP(6);
    SET _RemoveOn = DATE_ADD(_Now, INTERVAL _RemovalTimeout SECOND);

    START TRANSACTION;

    INSERT INTO OrleansStreamDeadLetter
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
    SELECT
        ServiceId,
        ProviderId,
        QueueId,
        MessageId,
        Dequeued,
        VisibleOn,
        ExpiresOn,
        CreatedOn,
        ModifiedOn,
        _Now AS DeadOn,
        _RemoveOn,
        Payload
    FROM
        OrleansStreamMessage
    WHERE
        ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
        AND MessageId = _MessageId
        AND
        (
            -- a message is dead if the last attempt timed out
            (Dequeued >= _MaxAttempts AND VisibleOn <= _Now)
            OR
            -- a message is dead if it expired regardless
            (ExpiresOn <= _Now)
        )
    FOR UPDATE;

    /* delete the source row if it was copied */
    IF ROW_COUNT() > 0 THEN

        DELETE FROM OrleansStreamMessage
        WHERE
            ServiceId = _ServiceId
            AND ProviderId = _ProviderId
            AND QueueId = _QueueId
            AND MessageId = _MessageId;

    END IF;

    /* run the select even if empty to ensure the resultset schema is always returned */
    SELECT
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
    FROM
        OrleansStreamDeadLetter
    WHERE
        ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
        AND MessageId = _MessageId;

    COMMIT;

END;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'EvictStreamMessageKey',
	'CALL EvictStreamMessage(@ServiceId, @ProviderId, @QueueId, @MessageId, @MaxAttempts, @RemovalTimeout)'

DELIMITER $$

CREATE PROCEDURE EvictStreamMessages
(
	IN _ServiceId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
	IN _QueueId NVARCHAR(150),
	IN _BatchSize INT,
	IN _MaxAttempts INT,
	IN _RemovalTimeout INT
)
BEGIN

DECLARE _Now DATETIME(6);
DECLARE _RemoveOn DATETIME(6);
DECLARE EXIT HANDLER FOR SQLEXCEPTION ROLLBACK;

SET _Now = UTC_TIMESTAMP();
SET _RemoveOn = DATE_ADD(_Now, INTERVAL _RemovalTimeout SECOND);

START TRANSACTION;

/* copy elected messages to a temporary table with updated flags */
CREATE TEMPORARY TABLE _Batch LIKE OrleansStreamDeadLetter;
INSERT INTO _Batch
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
    Payload,
    DeadOn,
    RemoveOn,
    Payload
)
SELECT
    ServiceId,
    ProviderId,
    QueueId,
    MessageId,
    Dequeued,
    VisibleOn,
    ExpiresOn,
    CreatedOn,
    ModifiedOn,
    DeadOn = _Now,
    RemoveOn = _RemoveOn,
    Payload
FROM
    OrleansStreamMessage
WHERE
    ServiceId = _ServiceId
    AND ProviderId = _ProviderId
    AND QueueId = _QueueId
    AND
	(
		-- a message is no longer dequeueable if the last attempt timed out
		(Dequeued >= _MaxAttempts AND VisibleOn <= _Now)
		OR
		-- a message is no longer dequeueable if it has expired regardless
		(ExpiresOn <= _Now)
	)
ORDER BY
    ServiceId,
    ProviderId,
    QueueId,
    MessageId
LIMIT _BatchSize
FOR UPDATE;

/* delete elected messages from the source now */
DELETE M
FROM OrleansStreamMessage AS M
INNER JOIN _Batch AS B
ON
    M.ServiceId = B.ServiceId
    AND M.ProviderId = B.ProviderId
    AND M.QueueId = B.QueueId
    AND M.MessageId = B.MessageId;

/* now insert the deleted messages with updated flags into the dead letter table */
INSERT INTO OrleansStreamDeadLetter
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
SELECT
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
FROM
    _Batch;

COMMIT;

END;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'EvictStreamMessagesKey',
	'CALL EvictStreamMessages(@ServiceId, @ProviderId, @QueueId, @BatchSize, @MaxAttempts, @RemovalTimeout)'
;

DELIMITER $$

CREATE PROCEDURE EvictStreamDeadLetters
(
	_ServiceId NVARCHAR(150),
    _ProviderId NVARCHAR(150),
	_QueueId NVARCHAR(150),
	_BatchSize INT
)
BEGIN

DECLARE _Now DATETIME(6);
DECLARE EXIT HANDLER FOR SQLEXCEPTION ROLLBACK;

SET _Now = UTC_TIMESTAMP();

DELETE FROM OrleansStreamDeadLetter
WHERE
    ServiceId = _ServiceId
    AND ProviderId = _ProviderId
    AND QueueId = _QueueId
    AND RemoveOn <= _Now
ORDER BY
    ServiceId,
    ProviderId,
    QueueId,
    MessageId
LIMIT _BatchSize;

END;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'EvictStreamDeadLettersKey',
	'CALL EvictStreamDeadLetters(@ServiceId, @ProviderId, @QueueId, @BatchSize)'
;

DELIMITER $$