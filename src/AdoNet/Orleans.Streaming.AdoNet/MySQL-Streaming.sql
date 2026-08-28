/*
ADO.NET streaming schema version 2.

This alpha schema is intentionally incompatible with the former destructive queue schema.
Drop the former streaming tables, sequence, routines, and OrleansQuery rows before applying
this script. Existing queue rows are not migrated.
*/

DROP PROCEDURE IF EXISTS ValidateOrleansStreamingSchemaUpgrade;

DELIMITER $$

CREATE PROCEDURE ValidateOrleansStreamingSchemaUpgrade()
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM information_schema.tables
        WHERE table_schema = DATABASE()
            AND table_name IN
            (
                'OrleansStreamPartition',
                'OrleansStreamMessage',
                'OrleansStreamDeadLetter',
                'OrleansStreamControl',
                'OrleansStreamMessageSequence'
            )
    )
    OR EXISTS
    (
        SELECT 1
        FROM OrleansQuery
        WHERE QueryKey IN
        (
            'QueueStreamMessageKey',
            'GetStreamMessagesKey',
            'ConfirmStreamMessagesKey',
            'FailStreamMessageKey',
            'EvictStreamMessagesKey',
            'EvictStreamDeadLettersKey',
            'StreamSchemaVersionKey'
        )
    )
    THEN
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = 'Incompatible alpha ADO.NET streaming schema. Drop old streaming objects and query rows; no in-place migration.';
    END IF;
END$$

DELIMITER ;

CALL ValidateOrleansStreamingSchemaUpgrade();
DROP PROCEDURE ValidateOrleansStreamingSchemaUpgrade;

CREATE TABLE OrleansStreamPartition
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    NextMessageId BIGINT NOT NULL,
    Checkpoint BIGINT NULL,
    OwnerEpoch BIGINT NOT NULL,
    CleanupOn DATETIME(6) NOT NULL,
    CreatedOn DATETIME(6) NOT NULL,
    ModifiedOn DATETIME(6) NOT NULL,

    PRIMARY KEY (ServiceId, ProviderId, QueueId)
) ENGINE = InnoDB;

CREATE TABLE OrleansStreamMessage
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    MessageId BIGINT NOT NULL,
    StreamIdBytes LONGBLOB NOT NULL,
    StreamNamespaceLength INT NOT NULL,
    CreatedOn DATETIME(6) NOT NULL,
    CheckpointedOn DATETIME(6) NULL,
    Payload LONGBLOB NOT NULL,

    PRIMARY KEY (ServiceId, ProviderId, QueueId, MessageId)
) ENGINE = InnoDB;

DELIMITER $$

CREATE PROCEDURE AppendStreamMessage
(
    IN _ServiceId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _QueueId NVARCHAR(150),
    IN _StreamIdBytes LONGBLOB,
    IN _StreamNamespaceLength INT,
    IN _Payload LONGBLOB,
    IN _ManageTransaction BOOLEAN
)
BEGIN
    DECLARE _Now DATETIME(6);
    DECLARE _MessageId BIGINT;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        IF _ManageTransaction THEN
            ROLLBACK;
        END IF;
        RESIGNAL;
    END;

    IF _ManageTransaction THEN
        START TRANSACTION;
    END IF;

    INSERT INTO OrleansStreamPartition
    (
        ServiceId,
        ProviderId,
        QueueId,
        NextMessageId,
        Checkpoint,
        OwnerEpoch,
        CleanupOn,
        CreatedOn,
        ModifiedOn
    )
    VALUES
    (
        _ServiceId,
        _ProviderId,
        _QueueId,
        1,
        NULL,
        0,
        UTC_TIMESTAMP(6),
        UTC_TIMESTAMP(6),
        UTC_TIMESTAMP(6)
    )
    ON DUPLICATE KEY UPDATE NextMessageId = NextMessageId;

    SELECT NextMessageId
    INTO _MessageId
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
    FOR UPDATE;

    SET _Now = UTC_TIMESTAMP(6);

    UPDATE OrleansStreamPartition
    SET
        NextMessageId = _MessageId + 1,
        ModifiedOn = _Now
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId;

    INSERT INTO OrleansStreamMessage
    (
        ServiceId,
        ProviderId,
        QueueId,
        MessageId,
        StreamIdBytes,
        StreamNamespaceLength,
        CreatedOn,
        Payload
    )
    VALUES
    (
        _ServiceId,
        _ProviderId,
        _QueueId,
        _MessageId,
        _StreamIdBytes,
        _StreamNamespaceLength,
        _Now,
        _Payload
    );

    IF _ManageTransaction THEN
        COMMIT;
    END IF;

    SELECT
        _ServiceId AS ServiceId,
        _ProviderId AS ProviderId,
        _QueueId AS QueueId,
        _MessageId AS MessageId;
END$$

CREATE PROCEDURE AcquireStreamPartition
(
    IN _ServiceId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _QueueId NVARCHAR(150),
    IN _StartFromNow BOOLEAN,
    IN _ManageTransaction BOOLEAN
)
BEGIN
    DECLARE _Now DATETIME(6);
    DECLARE _NextMessageId BIGINT;
    DECLARE _Checkpoint BIGINT;
    DECLARE _OwnerEpoch BIGINT;
    DECLARE _EarliestMessageId BIGINT;
    DECLARE _TailMessageId BIGINT;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        IF _ManageTransaction THEN
            ROLLBACK;
        END IF;
        RESIGNAL;
    END;

    IF _ManageTransaction THEN
        START TRANSACTION;
    END IF;

    INSERT INTO OrleansStreamPartition
    (
        ServiceId,
        ProviderId,
        QueueId,
        NextMessageId,
        Checkpoint,
        OwnerEpoch,
        CleanupOn,
        CreatedOn,
        ModifiedOn
    )
    VALUES
    (
        _ServiceId,
        _ProviderId,
        _QueueId,
        1,
        NULL,
        0,
        UTC_TIMESTAMP(6),
        UTC_TIMESTAMP(6),
        UTC_TIMESTAMP(6)
    )
    ON DUPLICATE KEY UPDATE NextMessageId = NextMessageId;

    SELECT NextMessageId, Checkpoint
    INTO _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
    FOR UPDATE;

    SET _Now = UTC_TIMESTAMP(6);

    SELECT MIN(MessageId), MAX(MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId;

    IF _Checkpoint IS NULL THEN
        SET _Checkpoint = CASE
            WHEN _StartFromNow THEN _NextMessageId - 1
            ELSE COALESCE(_EarliestMessageId - 1, _NextMessageId - 1)
        END;
    END IF;

    UPDATE OrleansStreamPartition
    SET
        Checkpoint = _Checkpoint,
        OwnerEpoch = OwnerEpoch + 1,
        ModifiedOn = _Now
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId;

    UPDATE OrleansStreamMessage
    SET CheckpointedOn = COALESCE(CheckpointedOn, _Now)
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
        AND MessageId <= _Checkpoint
        AND CheckpointedOn IS NULL;

    SELECT OwnerEpoch
    INTO _OwnerEpoch
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId;

    IF _ManageTransaction THEN
        COMMIT;
    END IF;

    SELECT
        _ServiceId AS ServiceId,
        _ProviderId AS ProviderId,
        _QueueId AS QueueId,
        _OwnerEpoch AS OwnerEpoch,
        _NextMessageId AS NextMessageId,
        _Checkpoint AS Checkpoint,
        _EarliestMessageId AS EarliestMessageId,
        _TailMessageId AS TailMessageId;
END$$

CREATE PROCEDURE AdvanceStreamCheckpoint
(
    IN _ServiceId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _QueueId NVARCHAR(150),
    IN _OwnerEpoch BIGINT,
    IN _Checkpoint BIGINT,
    IN _ManageTransaction BOOLEAN
)
BEGIN
    DECLARE _Now DATETIME(6);
    DECLARE _CurrentOwnerEpoch BIGINT;
    DECLARE _CurrentCheckpoint BIGINT;
    DECLARE _Updated BOOLEAN DEFAULT FALSE;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        IF _ManageTransaction THEN
            ROLLBACK;
        END IF;
        RESIGNAL;
    END;

    IF _ManageTransaction THEN
        START TRANSACTION;
    END IF;

    SELECT OwnerEpoch, Checkpoint
    INTO _CurrentOwnerEpoch, _CurrentCheckpoint
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
    FOR UPDATE;

    SET _Now = UTC_TIMESTAMP(6);

    UPDATE OrleansStreamPartition
    SET
        Checkpoint = _Checkpoint,
        ModifiedOn = _Now
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
        AND OwnerEpoch = _OwnerEpoch
        AND (Checkpoint IS NULL OR Checkpoint < _Checkpoint)
        AND _Checkpoint < NextMessageId;

    SET _Updated = ROW_COUNT() = 1;

    IF _Updated THEN
        UPDATE OrleansStreamMessage
        SET CheckpointedOn = COALESCE(CheckpointedOn, _Now)
        WHERE ServiceId = _ServiceId
            AND ProviderId = _ProviderId
            AND QueueId = _QueueId
            AND (_CurrentCheckpoint IS NULL OR MessageId > _CurrentCheckpoint)
            AND MessageId <= _Checkpoint
            AND CheckpointedOn IS NULL;
    END IF;

    SELECT OwnerEpoch, Checkpoint
    INTO _CurrentOwnerEpoch, _CurrentCheckpoint
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
    FOR UPDATE;

    IF _ManageTransaction THEN
        COMMIT;
    END IF;

    SELECT
        _ServiceId AS ServiceId,
        _ProviderId AS ProviderId,
        _QueueId AS QueueId,
        _CurrentOwnerEpoch AS OwnerEpoch,
        _CurrentCheckpoint AS Checkpoint,
        _Updated AS Updated
    FROM DUAL
    WHERE _CurrentOwnerEpoch IS NOT NULL;
END$$

CREATE PROCEDURE CleanupStreamMessages
(
    IN _ServiceId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _QueueId NVARCHAR(150),
    IN _RetentionPeriodSeconds INT,
    IN _MaximumRetentionPeriodSeconds INT,
    IN _CleanupIntervalSeconds INT,
    IN _CleanupBatchSize INT,
    IN _ManageTransaction BOOLEAN
)
BEGIN
    DECLARE _Now DATETIME(6) DEFAULT UTC_TIMESTAMP(6);
    DECLARE _Checkpoint BIGINT;
    DECLARE _CleanupOn DATETIME(6);
    DECLARE _DeletedCount INT DEFAULT 0;
    DECLARE _DeletedThroughMessageId BIGINT;
    DECLARE _HardDeletedCount INT DEFAULT 0;
    DECLARE _HardDeletedFromMessageId BIGINT;
    DECLARE _HardDeletedThroughMessageId BIGINT;
    DECLARE _EarliestMessageId BIGINT;
    DECLARE _TailMessageId BIGINT;
    DECLARE _PartitionExists BOOLEAN DEFAULT FALSE;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        IF _ManageTransaction THEN
            ROLLBACK;
        END IF;
        RESIGNAL;
    END;

    IF _ManageTransaction THEN
        START TRANSACTION;
    END IF;

    SELECT TRUE, Checkpoint, CleanupOn
    INTO _PartitionExists, _Checkpoint, _CleanupOn
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
    FOR UPDATE;

    IF NOT _PartitionExists OR _CleanupOn > _Now THEN
        SELECT MIN(MessageId), MAX(MessageId)
        INTO _EarliestMessageId, _TailMessageId
        FROM OrleansStreamMessage
        WHERE ServiceId = _ServiceId
            AND ProviderId = _ProviderId
            AND QueueId = _QueueId;

        IF _ManageTransaction THEN
            COMMIT;
        END IF;

        SELECT
            FALSE AS Ran,
            0 AS DeletedCount,
            NULL AS DeletedThroughMessageId,
            0 AS HardDeletedCount,
            NULL AS HardDeletedFromMessageId,
            NULL AS HardDeletedThroughMessageId,
            _Checkpoint AS Checkpoint,
            _EarliestMessageId AS EarliestMessageId,
            _TailMessageId AS TailMessageId;
    ELSE
        UPDATE OrleansStreamPartition
        SET
            CleanupOn = DATE_ADD(_Now, INTERVAL _CleanupIntervalSeconds SECOND),
            ModifiedOn = _Now
        WHERE ServiceId = _ServiceId
            AND ProviderId = _ProviderId
            AND QueueId = _QueueId;

        DROP TEMPORARY TABLE IF EXISTS OrleansStreamCleanupBatch;
        CREATE TEMPORARY TABLE OrleansStreamCleanupBatch
        (
            MessageId BIGINT NOT NULL,
            PRIMARY KEY (MessageId)
        );

        INSERT INTO OrleansStreamCleanupBatch (MessageId)
        SELECT MessageId
        FROM OrleansStreamMessage
        WHERE ServiceId = _ServiceId
            AND ProviderId = _ProviderId
            AND QueueId = _QueueId
            AND
            (
                (
                    _Checkpoint IS NOT NULL
                    AND MessageId <= _Checkpoint
                    AND CheckpointedOn < DATE_SUB(_Now, INTERVAL _RetentionPeriodSeconds SECOND)
                )
                OR
                (
                    _MaximumRetentionPeriodSeconds IS NOT NULL
                    AND CreatedOn < DATE_SUB(_Now, INTERVAL _MaximumRetentionPeriodSeconds SECOND)
                )
            )
        ORDER BY MessageId
        LIMIT _CleanupBatchSize
        FOR UPDATE;

        SELECT
            COUNT(*),
            MAX(MessageId),
            COALESCE(SUM(_Checkpoint IS NULL OR MessageId > _Checkpoint), 0),
            MIN(CASE WHEN _Checkpoint IS NULL OR MessageId > _Checkpoint THEN MessageId END),
            MAX(CASE WHEN _Checkpoint IS NULL OR MessageId > _Checkpoint THEN MessageId END)
        INTO
            _DeletedCount,
            _DeletedThroughMessageId,
            _HardDeletedCount,
            _HardDeletedFromMessageId,
            _HardDeletedThroughMessageId
        FROM OrleansStreamCleanupBatch;

        DELETE M
        FROM OrleansStreamMessage AS M
        INNER JOIN OrleansStreamCleanupBatch AS B
            ON B.MessageId = M.MessageId
        WHERE M.ServiceId = _ServiceId
            AND M.ProviderId = _ProviderId
            AND M.QueueId = _QueueId;

        SELECT MIN(MessageId), MAX(MessageId)
        INTO _EarliestMessageId, _TailMessageId
        FROM OrleansStreamMessage
        WHERE ServiceId = _ServiceId
            AND ProviderId = _ProviderId
            AND QueueId = _QueueId;

        DROP TEMPORARY TABLE OrleansStreamCleanupBatch;

        IF _ManageTransaction THEN
            COMMIT;
        END IF;

        SELECT
            TRUE AS Ran,
            _DeletedCount AS DeletedCount,
            _DeletedThroughMessageId AS DeletedThroughMessageId,
            _HardDeletedCount AS HardDeletedCount,
            _HardDeletedFromMessageId AS HardDeletedFromMessageId,
            _HardDeletedThroughMessageId AS HardDeletedThroughMessageId,
            _Checkpoint AS Checkpoint,
            _EarliestMessageId AS EarliestMessageId,
            _TailMessageId AS TailMessageId;
    END IF;
END$$

DELIMITER ;

INSERT INTO OrleansQuery (QueryKey, QueryText)
VALUES
    ('StreamSchemaVersionKey', '2'),
    ('AppendStreamMessageKey', 'CALL AppendStreamMessage(@ServiceId, @ProviderId, @QueueId, @StreamIdBytes, @StreamNamespaceLength, @Payload, TRUE)'),
    ('AcquireStreamPartitionKey', 'CALL AcquireStreamPartition(@ServiceId, @ProviderId, @QueueId, @StartFromNow, TRUE)'),
    ('ReadStreamMessagesKey', 'SELECT ServiceId, ProviderId, QueueId, MessageId, StreamIdBytes, StreamNamespaceLength, CreatedOn, Payload FROM OrleansStreamMessage WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId AND MessageId > @AfterMessageId ORDER BY MessageId LIMIT @MaxCount'),
    ('AdvanceStreamCheckpointKey', 'CALL AdvanceStreamCheckpoint(@ServiceId, @ProviderId, @QueueId, @OwnerEpoch, @Checkpoint, TRUE)'),
    ('GetStreamPartitionBoundsKey', 'SELECT P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.NextMessageId, P.Checkpoint, MIN(M.MessageId) AS EarliestMessageId, MAX(M.MessageId) AS TailMessageId FROM OrleansStreamPartition AS P LEFT JOIN OrleansStreamMessage AS M ON M.ServiceId = P.ServiceId AND M.ProviderId = P.ProviderId AND M.QueueId = P.QueueId WHERE P.ServiceId = @ServiceId AND P.ProviderId = @ProviderId AND P.QueueId = @QueueId GROUP BY P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.NextMessageId, P.Checkpoint'),
    ('CleanupStreamMessagesKey', 'CALL CleanupStreamMessages(@ServiceId, @ProviderId, @QueueId, @RetentionPeriodSeconds, @MaximumRetentionPeriodSeconds, @CleanupIntervalSeconds, @CleanupBatchSize, TRUE)');
