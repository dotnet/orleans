/*
ADO.NET streaming schema version 3.

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
                'OrleansStreamReplayLease',
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
    ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
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
    ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    MessageId BIGINT NOT NULL,
    StreamIdBytes LONGBLOB NOT NULL,
    StreamNamespaceLength INT NOT NULL,
    CreatedOn DATETIME(6) NOT NULL,
    CheckpointedOn DATETIME(6) NULL,
    Payload LONGBLOB NOT NULL,

    PRIMARY KEY (ServiceId, ProviderId, QueueId, MessageId)
) ENGINE = InnoDB;

CREATE TABLE OrleansStreamReplayLease
(
    ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    ReaderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    StreamIdBytes LONGBLOB NOT NULL,
    StreamNamespaceLength INT NOT NULL,
    OwnerEpoch BIGINT NOT NULL,
    Watermark BIGINT NOT NULL,
    ExpiresOn DATETIME(6) NOT NULL,
    CreatedOn DATETIME(6) NOT NULL,
    ModifiedOn DATETIME(6) NOT NULL,

    PRIMARY KEY (ServiceId, ProviderId, QueueId, ReaderId),
    INDEX IX_OrleansStreamReplayLease_Active
        (ServiceId, ProviderId, QueueId, ExpiresOn, Watermark)
) ENGINE = InnoDB;

DELIMITER $$

CREATE PROCEDURE AppendStreamMessage
(
    IN _ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
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
    IN _ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
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
    IN _ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
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

CREATE PROCEDURE AcquireStreamReplayLease
(
    IN _ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ReaderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _StreamIdBytes LONGBLOB,
    IN _StreamNamespaceLength INT,
    IN _OwnerEpoch BIGINT,
    IN _AfterMessageId BIGINT,
    IN _ReplayLeaseDurationSeconds INT,
    IN _ManageTransaction BOOLEAN
)
BEGIN
    DECLARE _Now DATETIME(6);
    DECLARE _CurrentOwnerEpoch BIGINT;
    DECLARE _NextMessageId BIGINT;
    DECLARE _Checkpoint BIGINT;
    DECLARE _EarliestMessageId BIGINT;
    DECLARE _TailMessageId BIGINT;
    DECLARE _LeaseOwnerEpoch BIGINT;
    DECLARE _Watermark BIGINT;
    DECLARE _ExpiresOn DATETIME(6);
    DECLARE _Status VARCHAR(32);

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        IF _ManageTransaction THEN ROLLBACK; END IF;
        RESIGNAL;
    END;

    IF _ManageTransaction THEN START TRANSACTION; END IF;

    SELECT OwnerEpoch, NextMessageId, Checkpoint
    INTO _CurrentOwnerEpoch, _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId AND QueueId = _QueueId
    FOR UPDATE;

    SET _Now = UTC_TIMESTAMP(6);

    SELECT OwnerEpoch, Watermark, ExpiresOn
    INTO _LeaseOwnerEpoch, _Watermark, _ExpiresOn
    FROM OrleansStreamReplayLease
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
        AND QueueId = _QueueId AND ReaderId = _ReaderId
    FOR UPDATE;

    SELECT MIN(MessageId), MAX(MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId AND QueueId = _QueueId
    FOR UPDATE;

    IF _CurrentOwnerEpoch IS NULL OR _CurrentOwnerEpoch <> _OwnerEpoch
        OR (_LeaseOwnerEpoch IS NOT NULL AND _LeaseOwnerEpoch <> _OwnerEpoch AND _ExpiresOn > _Now)
    THEN
        SET _Status = 'OwnershipLost';
    ELSEIF _AfterMessageId < COALESCE(_EarliestMessageId, _NextMessageId) - 1 THEN
        SET _Status = 'HistoryUnavailable';
    ELSE
        IF _LeaseOwnerEpoch IS NOT NULL
            AND (_LeaseOwnerEpoch <> _OwnerEpoch OR _ExpiresOn <= _Now)
        THEN
            DELETE FROM OrleansStreamReplayLease
            WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
                AND QueueId = _QueueId AND ReaderId = _ReaderId;
            SET _LeaseOwnerEpoch = NULL;
        END IF;

        SET _ExpiresOn = DATE_ADD(_Now, INTERVAL _ReplayLeaseDurationSeconds SECOND);
        IF _LeaseOwnerEpoch IS NULL THEN
            SET _Watermark = _AfterMessageId;
            INSERT INTO OrleansStreamReplayLease
            (
                ServiceId, ProviderId, QueueId, ReaderId, StreamIdBytes,
                StreamNamespaceLength, OwnerEpoch, Watermark, ExpiresOn, CreatedOn, ModifiedOn
            )
            VALUES
            (
                _ServiceId, _ProviderId, _QueueId, _ReaderId, _StreamIdBytes,
                _StreamNamespaceLength, _OwnerEpoch, _Watermark, _ExpiresOn, _Now, _Now
            );
        ELSE
            SET _Watermark = GREATEST(_Watermark, _AfterMessageId);
            UPDATE OrleansStreamReplayLease
            SET Watermark = _Watermark, ExpiresOn = _ExpiresOn, ModifiedOn = _Now
            WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
                AND QueueId = _QueueId AND ReaderId = _ReaderId AND OwnerEpoch = _OwnerEpoch;
        END IF;
        SET _Status = 'Acquired';
    END IF;

    IF _ManageTransaction THEN COMMIT; END IF;

    SELECT _Status AS Status, _ServiceId AS ServiceId, _ProviderId AS ProviderId,
        _QueueId AS QueueId, _ReaderId AS ReaderId, _CurrentOwnerEpoch AS OwnerEpoch,
        _Watermark AS Watermark, _ExpiresOn AS ExpiresOn, _NextMessageId AS NextMessageId,
        _Checkpoint AS Checkpoint, _EarliestMessageId AS EarliestMessageId,
        _TailMessageId AS TailMessageId;
END$$

CREATE PROCEDURE ReadStreamReplayMessages
(
    IN _ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ReaderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _OwnerEpoch BIGINT,
    IN _AfterMessageId BIGINT,
    IN _MaxCount INT,
    IN _ReplayLeaseDurationSeconds INT,
    IN _ManageTransaction BOOLEAN
)
BEGIN
    DECLARE _Now DATETIME(6);
    DECLARE _CurrentOwnerEpoch BIGINT;
    DECLARE _NextMessageId BIGINT;
    DECLARE _Checkpoint BIGINT;
    DECLARE _EarliestMessageId BIGINT;
    DECLARE _TailMessageId BIGINT;
    DECLARE _LeaseOwnerEpoch BIGINT;
    DECLARE _Watermark BIGINT;
    DECLARE _ExpiresOn DATETIME(6);
    DECLARE _Status VARCHAR(32);

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        IF _ManageTransaction THEN ROLLBACK; END IF;
        RESIGNAL;
    END;

    IF _ManageTransaction THEN START TRANSACTION; END IF;

    SELECT OwnerEpoch, NextMessageId, Checkpoint
    INTO _CurrentOwnerEpoch, _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId AND QueueId = _QueueId
    FOR UPDATE;
    SET _Now = UTC_TIMESTAMP(6);

    SELECT OwnerEpoch, Watermark, ExpiresOn
    INTO _LeaseOwnerEpoch, _Watermark, _ExpiresOn
    FROM OrleansStreamReplayLease
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
        AND QueueId = _QueueId AND ReaderId = _ReaderId
    FOR UPDATE;

    SELECT MIN(MessageId), MAX(MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId AND QueueId = _QueueId
    FOR UPDATE;

    IF _CurrentOwnerEpoch IS NULL OR _CurrentOwnerEpoch <> _OwnerEpoch
        OR _LeaseOwnerEpoch IS NULL OR _LeaseOwnerEpoch <> _OwnerEpoch
    THEN
        SET _Status = 'OwnershipLost';
    ELSEIF _ExpiresOn <= _Now THEN
        SET _Status = 'Expired';
    ELSEIF _AfterMessageId < COALESCE(_EarliestMessageId, _NextMessageId) - 1 THEN
        SET _Status = 'HistoryUnavailable';
    ELSE
        SET _ExpiresOn = DATE_ADD(_Now, INTERVAL _ReplayLeaseDurationSeconds SECOND);
        UPDATE OrleansStreamReplayLease
        SET ExpiresOn = _ExpiresOn, ModifiedOn = _Now
        WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
            AND QueueId = _QueueId AND ReaderId = _ReaderId AND OwnerEpoch = _OwnerEpoch;
        SET _Status = 'Active';
    END IF;

    IF _Status = 'Active' THEN
        SELECT _Status AS Status, _CurrentOwnerEpoch AS OwnerEpoch, _Watermark AS Watermark,
            _ExpiresOn AS ExpiresOn, _NextMessageId AS NextMessageId, _Checkpoint AS Checkpoint,
            _EarliestMessageId AS EarliestMessageId, _TailMessageId AS TailMessageId,
            R.MessageId, R.StreamIdBytes, R.StreamNamespaceLength, R.CreatedOn, R.Payload
        FROM
        (
            SELECT MessageId, StreamIdBytes, StreamNamespaceLength, CreatedOn, Payload
            FROM OrleansStreamMessage
            WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
                AND QueueId = _QueueId AND MessageId > _AfterMessageId
            ORDER BY MessageId
            LIMIT _MaxCount
        ) AS R
        UNION ALL
        SELECT _Status, _CurrentOwnerEpoch, _Watermark, _ExpiresOn, _NextMessageId,
            _Checkpoint, _EarliestMessageId, _TailMessageId, NULL, NULL, NULL, NULL, NULL
        FROM DUAL
        WHERE NOT EXISTS
        (
            SELECT 1 FROM OrleansStreamMessage
            WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
                AND QueueId = _QueueId AND MessageId > _AfterMessageId
        )
        ORDER BY MessageId;
    ELSE
        SELECT _Status AS Status, _CurrentOwnerEpoch AS OwnerEpoch, _Watermark AS Watermark,
            _ExpiresOn AS ExpiresOn, _NextMessageId AS NextMessageId, _Checkpoint AS Checkpoint,
            _EarliestMessageId AS EarliestMessageId, _TailMessageId AS TailMessageId,
            NULL AS MessageId, NULL AS StreamIdBytes, NULL AS StreamNamespaceLength,
            NULL AS CreatedOn, NULL AS Payload;
    END IF;

    IF _ManageTransaction THEN COMMIT; END IF;
END$$

CREATE PROCEDURE UpdateStreamReplayLease
(
    IN _ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ReaderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _OwnerEpoch BIGINT,
    IN _Watermark BIGINT,
    IN _ReplayLeaseDurationSeconds INT,
    IN _ManageTransaction BOOLEAN
)
BEGIN
    DECLARE _Now DATETIME(6);
    DECLARE _CurrentOwnerEpoch BIGINT;
    DECLARE _NextMessageId BIGINT;
    DECLARE _Checkpoint BIGINT;
    DECLARE _EarliestMessageId BIGINT;
    DECLARE _TailMessageId BIGINT;
    DECLARE _LeaseOwnerEpoch BIGINT;
    DECLARE _CurrentWatermark BIGINT;
    DECLARE _ExpiresOn DATETIME(6);
    DECLARE _Status VARCHAR(32);

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        IF _ManageTransaction THEN ROLLBACK; END IF;
        RESIGNAL;
    END;

    IF _ManageTransaction THEN START TRANSACTION; END IF;

    SELECT OwnerEpoch, NextMessageId, Checkpoint
    INTO _CurrentOwnerEpoch, _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId AND QueueId = _QueueId
    FOR UPDATE;
    SET _Now = UTC_TIMESTAMP(6);

    SELECT OwnerEpoch, Watermark, ExpiresOn
    INTO _LeaseOwnerEpoch, _CurrentWatermark, _ExpiresOn
    FROM OrleansStreamReplayLease
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
        AND QueueId = _QueueId AND ReaderId = _ReaderId
    FOR UPDATE;

    SELECT MIN(MessageId), MAX(MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId AND QueueId = _QueueId
    FOR UPDATE;

    IF _CurrentOwnerEpoch IS NULL OR _CurrentOwnerEpoch <> _OwnerEpoch
        OR _LeaseOwnerEpoch IS NULL OR _LeaseOwnerEpoch <> _OwnerEpoch
    THEN
        SET _Status = 'OwnershipLost';
    ELSEIF _ExpiresOn <= _Now THEN
        SET _Status = 'Expired';
    ELSEIF _Watermark < COALESCE(_EarliestMessageId, _NextMessageId) - 1 THEN
        SET _Status = 'HistoryUnavailable';
    ELSE
        SET _CurrentWatermark = GREATEST(_CurrentWatermark, _Watermark);
        SET _ExpiresOn = DATE_ADD(_Now, INTERVAL _ReplayLeaseDurationSeconds SECOND);
        UPDATE OrleansStreamReplayLease
        SET Watermark = _CurrentWatermark, ExpiresOn = _ExpiresOn, ModifiedOn = _Now
        WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
            AND QueueId = _QueueId AND ReaderId = _ReaderId AND OwnerEpoch = _OwnerEpoch;
        SET _Status = 'Active';
    END IF;

    IF _ManageTransaction THEN COMMIT; END IF;

    SELECT _Status AS Status, _CurrentOwnerEpoch AS OwnerEpoch,
        _CurrentWatermark AS Watermark, _ExpiresOn AS ExpiresOn,
        _NextMessageId AS NextMessageId, _Checkpoint AS Checkpoint,
        _EarliestMessageId AS EarliestMessageId, _TailMessageId AS TailMessageId;
END$$

CREATE PROCEDURE ReleaseStreamReplayLease
(
    IN _ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ReaderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _OwnerEpoch BIGINT,
    IN _ManageTransaction BOOLEAN
)
BEGIN
    DECLARE _CurrentOwnerEpoch BIGINT;
    DECLARE _NextMessageId BIGINT;
    DECLARE _Checkpoint BIGINT;
    DECLARE _EarliestMessageId BIGINT;
    DECLARE _TailMessageId BIGINT;
    DECLARE _LeaseOwnerEpoch BIGINT;
    DECLARE _Watermark BIGINT;
    DECLARE _ExpiresOn DATETIME(6);
    DECLARE _Status VARCHAR(32);

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        IF _ManageTransaction THEN ROLLBACK; END IF;
        RESIGNAL;
    END;

    IF _ManageTransaction THEN START TRANSACTION; END IF;

    SELECT OwnerEpoch, NextMessageId, Checkpoint
    INTO _CurrentOwnerEpoch, _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId AND QueueId = _QueueId
    FOR UPDATE;

    SELECT OwnerEpoch, Watermark, ExpiresOn
    INTO _LeaseOwnerEpoch, _Watermark, _ExpiresOn
    FROM OrleansStreamReplayLease
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
        AND QueueId = _QueueId AND ReaderId = _ReaderId
    FOR UPDATE;

    SELECT MIN(MessageId), MAX(MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId AND QueueId = _QueueId
    FOR UPDATE;

    IF _CurrentOwnerEpoch IS NULL OR _CurrentOwnerEpoch <> _OwnerEpoch
        OR (_LeaseOwnerEpoch IS NOT NULL AND _LeaseOwnerEpoch <> _OwnerEpoch)
    THEN
        SET _Status = 'OwnershipLost';
    ELSE
        DELETE FROM OrleansStreamReplayLease
        WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
            AND QueueId = _QueueId AND ReaderId = _ReaderId AND OwnerEpoch = _OwnerEpoch;
        SET _Status = 'Released';
    END IF;

    IF _ManageTransaction THEN COMMIT; END IF;

    SELECT _Status AS Status, _CurrentOwnerEpoch AS OwnerEpoch, _Watermark AS Watermark,
        _ExpiresOn AS ExpiresOn, _NextMessageId AS NextMessageId, _Checkpoint AS Checkpoint,
        _EarliestMessageId AS EarliestMessageId, _TailMessageId AS TailMessageId;
END$$

CREATE PROCEDURE CleanupStreamMessages
(
    IN _ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _ProviderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _QueueId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    IN _OwnerEpoch BIGINT,
    IN _RetentionPeriodSeconds INT,
    IN _MaximumRetentionPeriodSeconds INT,
    IN _CleanupIntervalSeconds INT,
    IN _CleanupBatchSize INT,
    IN _ManageTransaction BOOLEAN
)
BEGIN
    DECLARE _Now DATETIME(6);
    DECLARE _CurrentOwnerEpoch BIGINT;
    DECLARE _Checkpoint BIGINT;
    DECLARE _CleanupOn DATETIME(6);
    DECLARE _ActiveReplayWatermark BIGINT;
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

    SELECT TRUE, OwnerEpoch, Checkpoint, CleanupOn
    INTO _PartitionExists, _CurrentOwnerEpoch, _Checkpoint, _CleanupOn
    FROM OrleansStreamPartition
    WHERE ServiceId = _ServiceId
        AND ProviderId = _ProviderId
        AND QueueId = _QueueId
    FOR UPDATE;

    SET _Now = UTC_TIMESTAMP(6);

    SELECT MIN(Watermark)
    INTO _ActiveReplayWatermark
    FROM OrleansStreamReplayLease
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId AND QueueId = _QueueId
    FOR UPDATE;

    IF _CurrentOwnerEpoch = _OwnerEpoch THEN
        DELETE FROM OrleansStreamReplayLease
        WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
            AND QueueId = _QueueId AND ExpiresOn <= _Now;
    END IF;

    SELECT MIN(Watermark)
    INTO _ActiveReplayWatermark
    FROM OrleansStreamReplayLease
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId
        AND QueueId = _QueueId AND ExpiresOn > _Now;

    SELECT MIN(MessageId), MAX(MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage
    WHERE ServiceId = _ServiceId AND ProviderId = _ProviderId AND QueueId = _QueueId
    FOR UPDATE;

    IF NOT _PartitionExists OR _CurrentOwnerEpoch <> _OwnerEpoch OR _CleanupOn > _Now THEN

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
            _ActiveReplayWatermark AS ActiveReplayWatermark,
            _EarliestMessageId AS EarliestMessageId,
            _TailMessageId AS TailMessageId;
    ELSE
        UPDATE OrleansStreamPartition
        SET
            CleanupOn = DATE_ADD(_Now, INTERVAL _CleanupIntervalSeconds SECOND),
            ModifiedOn = _Now
        WHERE ServiceId = _ServiceId
            AND ProviderId = _ProviderId
            AND QueueId = _QueueId
            AND OwnerEpoch = _OwnerEpoch;

        DROP TEMPORARY TABLE IF EXISTS OrleansStreamCleanupBatch;
        CREATE TEMPORARY TABLE OrleansStreamCleanupBatch
        (
            MessageId BIGINT NOT NULL,
            HardDeleted BOOLEAN NOT NULL,
            PRIMARY KEY (MessageId)
        );

        INSERT INTO OrleansStreamCleanupBatch (MessageId, HardDeleted)
        SELECT MessageId,
            (
                _MaximumRetentionPeriodSeconds IS NOT NULL
                AND CreatedOn < DATE_SUB(_Now, INTERVAL _MaximumRetentionPeriodSeconds SECOND)
                AND NOT
                (
                    _Checkpoint IS NOT NULL
                    AND MessageId <= _Checkpoint
                    AND CheckpointedOn < DATE_SUB(_Now, INTERVAL _RetentionPeriodSeconds SECOND)
                    AND (_ActiveReplayWatermark IS NULL OR MessageId <= _ActiveReplayWatermark)
                )
            )
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
                    AND (_ActiveReplayWatermark IS NULL OR MessageId <= _ActiveReplayWatermark)
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
            COALESCE(SUM(HardDeleted), 0),
            MIN(CASE WHEN HardDeleted THEN MessageId END),
            MAX(CASE WHEN HardDeleted THEN MessageId END)
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
            _ActiveReplayWatermark AS ActiveReplayWatermark,
            _EarliestMessageId AS EarliestMessageId,
            _TailMessageId AS TailMessageId;
    END IF;
END$$

DELIMITER ;

INSERT INTO OrleansQuery (QueryKey, QueryText)
VALUES
    ('StreamSchemaVersionKey', '3'),
    ('AppendStreamMessageKey', 'CALL AppendStreamMessage(@ServiceId, @ProviderId, @QueueId, @StreamIdBytes, @StreamNamespaceLength, @Payload, TRUE)'),
    ('AcquireStreamPartitionKey', 'CALL AcquireStreamPartition(@ServiceId, @ProviderId, @QueueId, @StartFromNow, TRUE)'),
    ('ReadStreamMessagesKey', 'SELECT ServiceId, ProviderId, QueueId, MessageId, StreamIdBytes, StreamNamespaceLength, CreatedOn, Payload FROM OrleansStreamMessage WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId AND MessageId > @AfterMessageId ORDER BY MessageId LIMIT @MaxCount'),
    ('AdvanceStreamCheckpointKey', 'CALL AdvanceStreamCheckpoint(@ServiceId, @ProviderId, @QueueId, @OwnerEpoch, @Checkpoint, TRUE)'),
    ('GetStreamPartitionBoundsKey', 'SELECT P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.NextMessageId, P.Checkpoint, MIN(M.MessageId) AS EarliestMessageId, MAX(M.MessageId) AS TailMessageId FROM OrleansStreamPartition AS P LEFT JOIN OrleansStreamMessage AS M ON M.ServiceId = P.ServiceId AND M.ProviderId = P.ProviderId AND M.QueueId = P.QueueId WHERE P.ServiceId = @ServiceId AND P.ProviderId = @ProviderId AND P.QueueId = @QueueId GROUP BY P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.NextMessageId, P.Checkpoint'),
    ('AcquireStreamReplayLeaseKey', 'CALL AcquireStreamReplayLease(@ServiceId, @ProviderId, @QueueId, @ReaderId, @StreamIdBytes, @StreamNamespaceLength, @OwnerEpoch, @AfterMessageId, @ReplayLeaseDurationSeconds, TRUE)'),
    ('ReadStreamReplayMessagesKey', 'CALL ReadStreamReplayMessages(@ServiceId, @ProviderId, @QueueId, @ReaderId, @OwnerEpoch, @AfterMessageId, @MaxCount, @ReplayLeaseDurationSeconds, TRUE)'),
    ('UpdateStreamReplayLeaseKey', 'CALL UpdateStreamReplayLease(@ServiceId, @ProviderId, @QueueId, @ReaderId, @OwnerEpoch, @Watermark, @ReplayLeaseDurationSeconds, TRUE)'),
    ('ReleaseStreamReplayLeaseKey', 'CALL ReleaseStreamReplayLease(@ServiceId, @ProviderId, @QueueId, @ReaderId, @OwnerEpoch, TRUE)'),
    ('CleanupStreamMessagesKey', 'CALL CleanupStreamMessages(@ServiceId, @ProviderId, @QueueId, @OwnerEpoch, @RetentionPeriodSeconds, @MaximumRetentionPeriodSeconds, @CleanupIntervalSeconds, @CleanupBatchSize, TRUE)');
