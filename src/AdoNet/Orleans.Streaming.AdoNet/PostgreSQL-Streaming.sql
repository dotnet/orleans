CREATE SEQUENCE OrleansStreamMessageSequence
AS BIGINT
START WITH 1
INCREMENT BY 1
NO MAXVALUE
NO CYCLE;

CREATE TABLE OrleansStreamMessage
(
	ServiceId VARCHAR(150) NOT NULL,
    ProviderId VARCHAR(150) NOT NULL,
	QueueId VARCHAR(150) NOT NULL,
	MessageId BIGINT NOT NULL,
	Dequeued INT NOT NULL,
	VisibleOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
	ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
	CreatedOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
	ModifiedOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
	Payload BYTEA NOT NULL,

	CONSTRAINT PK_OrleansStreamMessage PRIMARY KEY
	(
		ServiceId,
        ProviderId,
		QueueId,
		MessageId
	)
);

CREATE TABLE OrleansStreamDeadLetter
(
	ServiceId VARCHAR(150) NOT NULL,
    ProviderId VARCHAR(150) NOT NULL,
	QueueId VARCHAR(150) NOT NULL,
	MessageId BIGINT NOT NULL,
	Dequeued INT NOT NULL,
	VisibleOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
	ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
	CreatedOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
	ModifiedOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
	DeadOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
	RemoveOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
	Payload BYTEA,

	CONSTRAINT PK_OrleansStreamDeadLetter PRIMARY KEY
    (
        ServiceId,
        ProviderId,
        QueueId,
        MessageId
    )
);

CREATE TABLE OrleansStreamControl
(
	ServiceId VARCHAR(150) NOT NULL,
    ProviderId VARCHAR(150) NOT NULL,
	QueueId VARCHAR(150) NOT NULL,
	EvictOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,

	CONSTRAINT PK_OrleansStreamControl PRIMARY KEY
    (
        ServiceId,
        ProviderId,
        QueueId
    )
);

CREATE OR REPLACE FUNCTION QueueStreamMessage
(
	_ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
	_QueueId VARCHAR(150),
	_Payload BYTEA,
	_ExpiryTimeout INT
)
RETURNS TABLE
(
	ServiceId VARCHAR(150),
    ProviderId VARCHAR(150),
	QueueId VARCHAR(150),
	MessageId BIGINT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
	_MessageId BIGINT := nextval('OrleansStreamMessageSequence');
	_Now TIMESTAMP(6) WITHOUT TIME ZONE := CURRENT_TIMESTAMP AT TIME ZONE 'UTC';
	_ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE := _Now + INTERVAL '1 SECOND' * _ExpiryTimeout;
BEGIN

RETURN QUERY
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
$$;

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'QueueStreamMessageKey',
	'SELECT * FROM QueueStreamMessage(@ServiceId, @ProviderId, @QueueId, @Payload, @ExpiryTimeout)'
;

CREATE OR REPLACE FUNCTION GetStreamMessages
(
	_ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
	_QueueId VARCHAR(150),
    _MaxCount INT,
	_MaxAttempts INT,
	_VisibilityTimeout INT,
    _RemovalTimeout INT,
    _EvictionInterval INT,
    _EvictionBatchSize INT
)
RETURNS TABLE
(
	ServiceId VARCHAR(150),
    ProviderId VARCHAR(150),
	QueueId VARCHAR(150),
	MessageId BIGINT,
	Dequeued INT,
	VisibleOn TIMESTAMP(6) WITHOUT TIME ZONE,
	ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE,
	CreatedOn TIMESTAMP(6) WITHOUT TIME ZONE,
	ModifiedOn TIMESTAMP(6) WITHOUT TIME ZONE,
	Payload BYTEA
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
	_Now TIMESTAMP(6) WITHOUT TIME ZONE := CURRENT_TIMESTAMP AT TIME ZONE 'UTC';
	_VisibleOn TIMESTAMP(6) WITHOUT TIME ZONE := _Now + INTERVAL '1 SECOND' * _VisibilityTimeout;
	_EvictOn TIMESTAMP(6) WITHOUT TIME ZONE;
    _NextEvictOn TIMESTAMP(6) WITHOUT TIME ZONE := _Now + INTERVAL '1 SECOND' * _EvictionInterval;
BEGIN

/* get the next eviction schedule */
SELECT EvictOn
INTO _EvictOn
FROM OrleansStreamControl
WHERE
	ServiceId = _ServiceId
	AND ProviderId = _ProviderId
	AND QueueId = _QueueId;

/* initialize the control row if necessary */
IF _EvictOn IS NULL THEN

    /* initialize with a past date so eviction runs immediately */
    INSERT INTO OrleansStreamControl
    (
        ServiceId,
        ProviderId,
        QueueId,
        EvictOn
    )
    VALUES
    (
        _ServiceId,
        _ProviderId,
        _QueueId,
        _Now - INTERVAL '1 SECOND'
    )
    ON CONFLICT (ServiceId, ProviderId, QueueId)
    DO NOTHING;

    /* get the next eviction schedule again */
    SELECT EvictOn
    INTO _EvictOn
    FROM OrleansStreamControl
    WHERE
	    ServiceId = _ServiceId
	    AND ProviderId = _ProviderId
	    AND QueueId = _QueueId;

END IF;

/* evict messages if necessary */
IF _EvictOn <= _Now THEN

    /* race to set the next schedule */
	UPDATE OrleansStreamControl
	SET EvictOn = _NextEvictOn
    WHERE
	    ServiceId = _ServiceId
		AND ProviderId = _ProviderId
		AND QueueId = _QueueId
		AND EvictOn <= _Now;

    /* if we won the race then we also run the due eviction */
	IF (FOUND) THEN
		CALL EvictStreamMessages(_ServiceId, _ProviderId, _QueueId, _EvictionBatchSize, _MaxAttempts, _RemovalTimeout);
		CALL EvictStreamDeadLetters(_ServiceId, _ProviderId, _QueueId, _EvictionBatchSize);
	END IF;

END IF;

RETURN QUERY
WITH Batch AS
(
    /* elect the next batch of visible messages */
	SELECT
		ServiceId,
		ProviderId,
		QueueId,
		MessageId
	FROM
		OrleansStreamMessage
	WHERE
		ServiceId = _ServiceId
		AND ProviderId = _ProviderId
		AND QueueId = _QueueId
		AND Dequeued < _MaxAttempts
		AND VisibleOn <= _Now
		AND ExpiresOn > _Now

    /* the criteria below helps prevent deadlocks while improving queue-like throughput */
	ORDER BY
		ServiceId,
		ProviderId,
		QueueId,
		MessageId
    FOR UPDATE
	LIMIT _MaxCount
)
UPDATE OrleansStreamMessage AS M
SET
	Dequeued = Dequeued + 1,
	VisibleOn = _VisibleOn,
	ModifiedOn = _Now
FROM
    Batch AS B
WHERE
	M.ServiceId = B.ServiceId
	AND M.ProviderId = B.ProviderId
	AND M.QueueId = B.QueueId
	AND M.MessageId = B.MessageId
RETURNING
    M.ServiceId,
    M.ProviderId,
    M.QueueId,
    M.MessageId,
    M.Dequeued,
    M.VisibleOn,
    M.ExpiresOn,
    M.CreatedOn,
    M.ModifiedOn,
    M.Payload;

END;
$$;

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'GetStreamMessagesKey',
	'SELECT * FROM GetStreamMessages(@ServiceId, @ProviderId, @QueueId, @MaxCount, @MaxAttempts, @VisibilityTimeout, @RemovalTimeout, @EvictionInterval, @EvictionBatchSize)'
;

CREATE OR REPLACE FUNCTION ConfirmStreamMessages
(
	_ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
	_QueueId VARCHAR(150),
    _Items TEXT
)
RETURNS TABLE
(
	ServiceId VARCHAR(150),
    ProviderId VARCHAR(150),
	QueueId VARCHAR(150),
	MessageId BIGINT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
	_Count INT;
BEGIN

CREATE TEMP TABLE _ItemsTable
(
	MessageId BIGINT PRIMARY KEY NOT NULL,
	Dequeued INT NOT NULL
) ON COMMIT DROP;

INSERT INTO _ItemsTable
(
	MessageId,
	Dequeued
)
SELECT
	CAST(split_part(Value, ':', 1) AS BIGINT) AS MessageId,
	CAST(split_part(Value, ':', 2) AS INT) AS Dequeued
FROM
	UNNEST(string_to_array(_Items, '|')) AS Value;

RETURN QUERY
WITH Batch AS
(
	SELECT
		M.*
	FROM
		OrleansStreamMessage AS M
        INNER JOIN _ItemsTable AS I
            ON I.MessageId = M.MessageId
            AND I.Dequeued = M.Dequeued
	WHERE
		ServiceId = _ServiceId
	    AND ProviderId = _ProviderId
		AND QueueId = _QueueId

    /* the criteria below helps prevent deadlocks */
	ORDER BY
	    ServiceId,
	    ProviderId,
	    QueueId,
		MessageId
    FOR UPDATE
)
DELETE FROM OrleansStreamMessage AS M
USING Batch AS B
WHERE
    M.ServiceId = B.ServiceId
    AND M.ProviderId = B.ProviderId
    AND M.QueueId = B.QueueId
    AND M.MessageId = B.MessageId
RETURNING
    M.ServiceId,
    M.ProviderId,
    M.QueueId,
    M.MessageId;

END;
$$;

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'ConfirmStreamMessagesKey',
	'SELECT * FROM ConfirmStreamMessages(@ServiceId, @ProviderId, @QueueId, @Items)'
;

CREATE OR REPLACE PROCEDURE FailStreamMessage
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _MessageId BIGINT,
    _MaxAttempts INT,
    _RemovalTimeout INT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMP(6) WITHOUT TIME ZONE := CURRENT_TIMESTAMP AT TIME ZONE 'UTC';
    _RemoveOn TIMESTAMP(6) WITHOUT TIME ZONE := _Now + INTERVAL '1 SECOND' * _RemovalTimeout;
BEGIN

/* if the message can still be dequeued then attempt to mark it visible again */
UPDATE OrleansStreamMessage
SET
    VisibleOn = _Now,
    ModifiedOn = _Now
WHERE
    ServiceId = _ServiceId
    AND ProviderId = _ProviderId
    AND QueueId = _QueueId
    AND MessageId = _MessageId
    AND Dequeued < _MaxAttempts;

IF FOUND THEN
    RETURN;
END IF;

/* otherwise attempt to move the message to dead letters */
WITH Deleted AS
(
    DELETE FROM OrleansStreamMessage
    WHERE
        ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
        AND MessageId = _MessageId
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
        Payload
)
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
    _RemoveOn AS RemoveOn,
    Payload
FROM
    Deleted;

END;
$$;

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'FailStreamMessageKey',
	'CALL FailStreamMessage(@ServiceId, @ProviderId, @QueueId, @MessageId, @MaxAttempts, @RemovalTimeout)'
;

CREATE OR REPLACE PROCEDURE EvictStreamMessages
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _BatchSize INT,
    _MaxAttempts INT,
    _RemovalTimeout INT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMP(6) WITHOUT TIME ZONE := CURRENT_TIMESTAMP AT TIME ZONE 'UTC';
    _RemoveOn TIMESTAMP(6) WITHOUT TIME ZONE := _Now + INTERVAL '1 second' * _RemovalTimeout;
BEGIN

/* elect the next batch of messages to evict */
WITH Batch AS
(
    SELECT
        ServiceId,
        ProviderId,
        QueueId,
        MessageId
    FROM
        OrleansStreamMessage
    WHERE
        ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId

        -- the message was given the opportunity to complete
        AND VisibleOn <= _Now
		AND
		(
			-- the message was dequeued too many times
			Dequeued >= _MaxAttempts
			OR
			-- the message expired
			ExpiresOn <= _Now
		)

    /* the criteria below helps prevent deadlocks while improving queue-like throughput */
    ORDER BY
        ServiceId,
        ProviderId,
        QueueId,
        MessageId
    FOR UPDATE
    LIMIT _BatchSize
),

/* delete the messages locked in the batch */
Deleted AS
(
    DELETE FROM OrleansStreamMessage AS M
    USING Batch AS B
    WHERE
        M.ServiceId = B.ServiceId
        AND M.ProviderId = B.ProviderId
        AND M.QueueId = B.QueueId
        AND M.MessageId = B.MessageId
    RETURNING
        M.ServiceId,
        M.ProviderId,
        M.QueueId,
        M.MessageId,
        M.Dequeued,
        M.VisibleOn,
        M.ExpiresOn,
        M.CreatedOn,
        M.ModifiedOn,
        M.Payload
)

/* copy the deleted messages to the dead-letter table */
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
    _Now,
    _RemoveOn,
    Payload
FROM
    Deleted AS D;

END;
$$;

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'EvictStreamMessagesKey',
	'CALL EvictStreamMessages(@ServiceId, @ProviderId, @QueueId, @BatchSize, @MaxAttempts, @RemovalTimeout)'
;

CREATE OR REPLACE PROCEDURE EvictStreamDeadLetters
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _BatchSize INT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMP(6) WITHOUT TIME ZONE := CURRENT_TIMESTAMP AT TIME ZONE 'UTC';
BEGIN

/* elect the next batch of dead letters to evict */
WITH Batch AS
(
    SELECT
        ServiceId,
        ProviderId,
        QueueId,
        MessageId
    FROM
        OrleansStreamDeadLetter
    WHERE
        ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
        AND RemoveOn <= _Now

    /* the criteria below helps prevent deadlocks while improving queue-like throughput */
    ORDER BY
        ServiceId,
        ProviderId,
        QueueId,
        MessageId
    FOR UPDATE
    LIMIT _BatchSize
)
DELETE FROM OrleansStreamDeadLetter AS M
USING Batch AS B
WHERE
    M.ServiceId = B.ServiceId
    AND M.ProviderId = B.ProviderId
    AND M.QueueId = B.QueueId
    AND M.MessageId = B.MessageId;

END;
$$;

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'EvictStreamDeadLettersKey',
	'CALL EvictStreamDeadLetters(@ServiceId, @ProviderId, @QueueId, @BatchSize)'
;