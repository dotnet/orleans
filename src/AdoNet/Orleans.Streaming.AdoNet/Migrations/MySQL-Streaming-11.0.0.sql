-- Run this migration for upgrading the MySQL streaming procedures for deployments created before 11.0.0.
-- Stop all silos using this streaming database before running the script, and restart them after it completes.
-- Procedure replacement uses separate DDL statements which commit independently. If interrupted, rerun the
-- complete script before restarting silos. Existing queued messages and dead letters are preserved.
-- MySQL 8.4 rejects CREATE TEMPORARY TABLE ... SELECT ... FOR UPDATE, so these procedures now create their
-- batch table first and fill it with a separate locking INSERT ... SELECT.
-- GetStreamMessages also passes its eviction batch size, max attempts and removal timeout to EvictStreamMessages
-- in the order that procedure declares them.

DROP PROCEDURE IF EXISTS GetStreamMessages;
DROP PROCEDURE IF EXISTS ConfirmStreamMessages;
DROP PROCEDURE IF EXISTS EvictStreamMessages;
DROP PROCEDURE IF EXISTS EvictStreamDeadLetters;

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

DECLARE _Now DATETIME(6) DEFAULT UTC_TIMESTAMP(6);
DECLARE _VisibleOn DATETIME(6) DEFAULT DATE_ADD(_Now, INTERVAL _VisibilityTimeout SECOND);
DECLARE _NextEvictOn TIMESTAMP(6) DEFAULT DATE_ADD(_Now, INTERVAL _EvictionInterval SECOND);
DECLARE _EvictOn DATETIME(6);
DECLARE _Count INT;

-- get the next eviction schedule
SET _EvictOn =
(
    SELECT EvictOn
    FROM OrleansStreamControl
    WHERE
        ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
);

-- initialize the control row as necessary
IF _EvictOn IS NULL THEN

    -- race to initialize the control row
    INSERT OrleansStreamControl
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
        _NextEvictOn
    )
    ON DUPLICATE KEY
    UPDATE
        EvictOn = EvictOn;

    -- read the winning update
    SET _EvictOn =
    (
        SELECT EvictOn
        FROM OrleansStreamControl
        WHERE
            ServiceId = _ServiceId
            AND ProviderId = _ProviderId
            AND QueueId = _QueueId
    );

END IF;

IF _EvictOn <= _Now THEN

    -- race to update the control row
    UPDATE OrleansStreamControl
    SET EvictOn = _NextEvictOn
    WHERE
        ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
        AND EvictOn <= _Now;

    -- if we won the race then we also run eviction
    IF ROW_COUNT() > 0 THEN
        CALL EvictStreamMessages(_ServiceId, _ProviderId, _QueueId, _EvictionBatchSize, _MaxAttempts, _RemovalTimeout);
        CALL EvictStreamDeadLetters(_ServiceId, _ProviderId, _QueueId, _EvictionBatchSize);
    END IF;

END IF;

START TRANSACTION;

/* elect the batch of messages to dequeue and lock them in order */
CREATE TEMPORARY TABLE _Batch
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    MessageId BIGINT NOT NULL
);

INSERT INTO _Batch (ServiceId, ProviderId, QueueId, MessageId)
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
ORDER BY
    ServiceId,
    ProviderId,
    QueueId,
    MessageId
LIMIT _MaxCount
FOR UPDATE SKIP LOCKED;

/* update the message batch */
UPDATE OrleansStreamMessage AS M
INNER JOIN _Batch AS B
    ON M.ServiceId = B.ServiceId
    AND M.ProviderId = B.ProviderId
    AND M.QueueId = B.QueueId
    AND M.MessageId = B.MessageId
SET
    M.Dequeued = M.Dequeued + 1,
    M.VisibleOn = _VisibleOn,
    M.ModifiedOn = _Now;

/* return the updated batch */
SELECT
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
FROM
    OrleansStreamMessage AS M
    INNER JOIN _Batch AS B
        ON M.ServiceId = B.ServiceId
        AND M.ProviderId = B.ProviderId
        AND M.QueueId = B.QueueId
        AND M.MessageId = B.MessageId
ORDER BY
    M.MessageId;

DROP TEMPORARY TABLE _Batch;

COMMIT;

END$$

DELIMITER $$

CREATE PROCEDURE ConfirmStreamMessages
(
    IN _ServiceId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _QueueId NVARCHAR(150),
    IN _Items LONGTEXT
)
BEGIN

DECLARE _Delimiter1 NVARCHAR(1) DEFAULT '|';
DECLARE _Delimiter2 NVARCHAR(1) DEFAULT ':';
DECLARE _Value LONGTEXT;
DECLARE _MessageId BIGINT;
DECLARE _Dequeued INT;

SET _Items = CONCAT(_Items, _Delimiter1);

/* parse the message identifiers to be deleted */
DROP TEMPORARY TABLE IF EXISTS _ItemsTable;
CREATE TEMPORARY TABLE _ItemsTable
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    MessageId BIGINT NOT NULL,
    Dequeued INT NOT NULL,

    PRIMARY KEY (ServiceId, ProviderId, QueueId, MessageId)
);

WHILE LOCATE(_Delimiter1, _Items) > 0 DO

    SET _Value = SUBSTRING_INDEX(_Items, _Delimiter1, 1);
    SET _MessageId = CAST(SUBSTRING_INDEX(_Value, _Delimiter2, 1) AS UNSIGNED);
    SET _Dequeued = CAST(SUBSTRING_INDEX(_Value, _Delimiter2, -1) AS SIGNED);

    INSERT INTO _ItemsTable
    (
        ServiceId,
        ProviderId,
        QueueId,
        MessageId,
        Dequeued
    )
    VALUES
    (
        _ServiceId,
        _ProviderId,
        _QueueId,
        _MessageId,
        _Dequeued
    );

    SET _Items = SUBSTRING(_Items, LOCATE(_Delimiter1, _Items) + 1);

END WHILE;

START TRANSACTION;

/* elect the batch of messages to confirm and lock them in order */
CREATE TEMPORARY TABLE _Batch
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    MessageId BIGINT NOT NULL
);

INSERT INTO _Batch (ServiceId, ProviderId, QueueId, MessageId)
SELECT
	M.ServiceId,
    M.ProviderId,
    M.QueueId,
    M.MessageId
FROM
	OrleansStreamMessage AS M
    INNER JOIN _ItemsTable AS I
        ON M.ServiceId = I.ServiceId
        AND M.ProviderId = I.ProviderId
        AND M.QueueId = I.QueueId
        AND M.MessageId = I.MessageId
        AND M.Dequeued = ABS(I.Dequeued)
ORDER BY
    M.ServiceId,
    M.ProviderId,
    M.QueueId,
	M.MessageId
FOR UPDATE;

IF EXISTS (SELECT 1 FROM _ItemsTable WHERE Dequeued < 0) THEN
    /* negative dequeue receipts release messages for immediate redelivery */
    UPDATE OrleansStreamMessage AS M
    INNER JOIN _Batch AS B
        ON M.ServiceId = B.ServiceId
        AND M.ProviderId = B.ProviderId
        AND M.QueueId = B.QueueId
        AND M.MessageId = B.MessageId
    SET
        M.VisibleOn = UTC_TIMESTAMP(6),
        M.ModifiedOn = UTC_TIMESTAMP(6);
ELSE
    /* delete the elected batch */
    DELETE M
    FROM OrleansStreamMessage AS M
    INNER JOIN _Batch AS B
        ON M.ServiceId = B.ServiceId
        AND M.ProviderId = B.ProviderId
        AND M.QueueId = B.QueueId
        AND M.MessageId = B.MessageId;
END IF;

/* return the ack */
SELECT
    ServiceId,
    ProviderId,
    QueueId,
    MessageId
FROM
    _Batch;

DROP TEMPORARY TABLE _Batch;
DROP TEMPORARY TABLE _ItemsTable;

COMMIT;
END$$

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

DECLARE _Now DATETIME(6) DEFAULT UTC_TIMESTAMP();
DECLARE _RemoveOn DATETIME(6) DEFAULT DATE_ADD(_Now, INTERVAL _RemovalTimeout SECOND);

START TRANSACTION;

/* elect the batch of messages to move and lock them in order */
CREATE TEMPORARY TABLE _Batch
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    MessageId BIGINT NOT NULL
);

INSERT INTO _Batch (ServiceId, ProviderId, QueueId, MessageId)
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
FOR UPDATE SKIP LOCKED;

/* copy the messages to dead letters */
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
    M.ServiceId,
    M.ProviderId,
    M.QueueId,
    M.MessageId,
    M.Dequeued,
    M.VisibleOn,
    M.ExpiresOn,
    M.CreatedOn,
    M.ModifiedOn,
    _Now,
    _RemoveOn,
    M.Payload
FROM
    OrleansStreamMessage AS M
    INNER JOIN _Batch AS B
        ON M.ServiceId = B.ServiceId
        AND M.ProviderId = B.ProviderId
        AND M.QueueId = B.QueueId
        AND M.MessageId = B.MessageId;

/* delete elected messages from the source now */
DELETE M
FROM OrleansStreamMessage AS M
INNER JOIN _Batch AS B
    ON M.ServiceId = B.ServiceId
    AND M.ProviderId = B.ProviderId
    AND M.QueueId = B.QueueId
    AND M.MessageId = B.MessageId;

DROP TEMPORARY TABLE _Batch;

COMMIT;

END$$

DELIMITER $$

CREATE PROCEDURE EvictStreamDeadLetters
(
	_ServiceId NVARCHAR(150),
    _ProviderId NVARCHAR(150),
	_QueueId NVARCHAR(150),
	_BatchSize INT
)
BEGIN

DECLARE _Now DATETIME(6) DEFAULT UTC_TIMESTAMP();

/* elect the batch of messages to remove */
CREATE TEMPORARY TABLE _Batch
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    MessageId BIGINT NOT NULL
);

INSERT INTO _Batch (ServiceId, ProviderId, QueueId, MessageId)
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
ORDER BY
    ServiceId,
    ProviderId,
    QueueId,
    MessageId
LIMIT _BatchSize
FOR UPDATE SKIP LOCKED;

/* now delete the locked messages */
DELETE M
FROM OrleansStreamDeadLetter AS M
INNER JOIN _Batch AS B
    ON M.ServiceId = B.ServiceId
    AND M.ProviderId = B.ProviderId
    AND M.QueueId = B.QueueId
    AND M.MessageId = B.MessageId;

DROP TEMPORARY TABLE _Batch;

END$$

DELIMITER ;
