/*
ADO.NET streaming schema version 2.

This alpha schema is intentionally incompatible with the former destructive queue schema.
Drop the former streaming tables, sequence, routines, and OrleansQuery rows before applying
this script. Existing queue rows are not migrated.
*/

DO $$
BEGIN
    IF to_regclass('orleansstreampartition') IS NOT NULL
        OR to_regclass('orleansstreammessage') IS NOT NULL
        OR to_regclass('orleansstreamdeadletter') IS NOT NULL
        OR to_regclass('orleansstreamcontrol') IS NOT NULL
        OR to_regclass('orleansstreammessagesequence') IS NOT NULL
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
        RAISE EXCEPTION 'Incompatible alpha ADO.NET streaming schema. Drop old streaming tables, sequence, routines, and OrleansQuery rows before applying version 2; no in-place migration is supported.';
    END IF;
END;
$$;

CREATE TABLE OrleansStreamPartition
(
    ServiceId VARCHAR(150) NOT NULL,
    ProviderId VARCHAR(150) NOT NULL,
    QueueId VARCHAR(150) NOT NULL,
    NextMessageId BIGINT NOT NULL,
    Checkpoint BIGINT NULL,
    OwnerEpoch BIGINT NOT NULL,
    CleanupOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
    CreatedOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
    ModifiedOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,

    CONSTRAINT PK_OrleansStreamPartition PRIMARY KEY
    (
        ServiceId,
        ProviderId,
        QueueId
    )
);

CREATE TABLE OrleansStreamMessage
(
    ServiceId VARCHAR(150) NOT NULL,
    ProviderId VARCHAR(150) NOT NULL,
    QueueId VARCHAR(150) NOT NULL,
    MessageId BIGINT NOT NULL,
    StreamIdBytes BYTEA NOT NULL,
    StreamNamespaceLength INT NOT NULL,
    CreatedOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
    CheckpointedOn TIMESTAMP(6) WITHOUT TIME ZONE NULL,
    Payload BYTEA NOT NULL,

    CONSTRAINT PK_OrleansStreamMessage PRIMARY KEY
    (
        ServiceId,
        ProviderId,
        QueueId,
        MessageId
    )
);

CREATE OR REPLACE FUNCTION AppendStreamMessage
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _StreamIdBytes BYTEA,
    _StreamNamespaceLength INT,
    _Payload BYTEA
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
    _Now TIMESTAMP(6) WITHOUT TIME ZONE := CURRENT_TIMESTAMP AT TIME ZONE 'UTC';
    _MessageId BIGINT;
BEGIN
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
        _Now,
        _Now,
        _Now
    )
    ON CONFLICT (ServiceId, ProviderId, QueueId) DO NOTHING;

    UPDATE OrleansStreamPartition AS P
    SET
        NextMessageId = P.NextMessageId + 1,
        ModifiedOn = _Now
    WHERE P.ServiceId = _ServiceId
        AND P.ProviderId = _ProviderId
        AND P.QueueId = _QueueId
    RETURNING P.NextMessageId - 1 INTO _MessageId;

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

    RETURN QUERY
    SELECT _ServiceId, _ProviderId, _QueueId, _MessageId;
END;
$$;

CREATE OR REPLACE FUNCTION AcquireStreamPartition
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _StartFromNow BOOLEAN
)
RETURNS TABLE
(
    ServiceId VARCHAR(150),
    ProviderId VARCHAR(150),
    QueueId VARCHAR(150),
    OwnerEpoch BIGINT,
    Checkpoint BIGINT,
    EarliestMessageId BIGINT,
    TailMessageId BIGINT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMP(6) WITHOUT TIME ZONE := CURRENT_TIMESTAMP AT TIME ZONE 'UTC';
    _NextMessageId BIGINT;
    _Checkpoint BIGINT;
    _OwnerEpoch BIGINT;
    _EarliestMessageId BIGINT;
    _TailMessageId BIGINT;
BEGIN
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
        _Now,
        _Now,
        _Now
    )
    ON CONFLICT (ServiceId, ProviderId, QueueId) DO NOTHING;

    SELECT P.NextMessageId, P.Checkpoint
    INTO _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition AS P
    WHERE P.ServiceId = _ServiceId
        AND P.ProviderId = _ProviderId
        AND P.QueueId = _QueueId
    FOR UPDATE;

    SELECT MIN(M.MessageId), MAX(M.MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId
        AND M.ProviderId = _ProviderId
        AND M.QueueId = _QueueId;

    IF _Checkpoint IS NULL THEN
        _Checkpoint := CASE
            WHEN _StartFromNow THEN _NextMessageId - 1
            ELSE COALESCE(_EarliestMessageId - 1, _NextMessageId - 1)
        END;
    END IF;

    UPDATE OrleansStreamPartition AS P
    SET
        Checkpoint = _Checkpoint,
        OwnerEpoch = P.OwnerEpoch + 1,
        ModifiedOn = _Now
    WHERE P.ServiceId = _ServiceId
        AND P.ProviderId = _ProviderId
        AND P.QueueId = _QueueId
    RETURNING P.OwnerEpoch INTO _OwnerEpoch;

    UPDATE OrleansStreamMessage AS M
    SET CheckpointedOn = COALESCE(M.CheckpointedOn, _Now)
    WHERE M.ServiceId = _ServiceId
        AND M.ProviderId = _ProviderId
        AND M.QueueId = _QueueId
        AND M.MessageId <= _Checkpoint;

    RETURN QUERY
    SELECT
        _ServiceId,
        _ProviderId,
        _QueueId,
        _OwnerEpoch,
        _Checkpoint,
        _EarliestMessageId,
        _TailMessageId;
END;
$$;

CREATE OR REPLACE FUNCTION AdvanceStreamCheckpoint
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _OwnerEpoch BIGINT,
    _Checkpoint BIGINT
)
RETURNS TABLE
(
    ServiceId VARCHAR(150),
    ProviderId VARCHAR(150),
    QueueId VARCHAR(150),
    OwnerEpoch BIGINT,
    Checkpoint BIGINT,
    Updated BOOLEAN
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _CurrentOwnerEpoch BIGINT;
    _CurrentCheckpoint BIGINT;
    _Updated BOOLEAN := FALSE;
BEGIN
    UPDATE OrleansStreamPartition AS P
    SET
        Checkpoint = _Checkpoint,
        ModifiedOn = CURRENT_TIMESTAMP AT TIME ZONE 'UTC'
    WHERE P.ServiceId = _ServiceId
        AND P.ProviderId = _ProviderId
        AND P.QueueId = _QueueId
        AND P.OwnerEpoch = _OwnerEpoch
        AND (P.Checkpoint IS NULL OR P.Checkpoint < _Checkpoint)
        AND _Checkpoint < P.NextMessageId
    RETURNING P.OwnerEpoch, P.Checkpoint
    INTO _CurrentOwnerEpoch, _CurrentCheckpoint;

    IF FOUND THEN
        _Updated := TRUE;
        UPDATE OrleansStreamMessage AS M
        SET CheckpointedOn = COALESCE(M.CheckpointedOn, CURRENT_TIMESTAMP AT TIME ZONE 'UTC')
        WHERE M.ServiceId = _ServiceId
            AND M.ProviderId = _ProviderId
            AND M.QueueId = _QueueId
            AND M.MessageId <= _Checkpoint;
    ELSE
        SELECT P.OwnerEpoch, P.Checkpoint
        INTO _CurrentOwnerEpoch, _CurrentCheckpoint
        FROM OrleansStreamPartition AS P
        WHERE P.ServiceId = _ServiceId
            AND P.ProviderId = _ProviderId
            AND P.QueueId = _QueueId;
    END IF;

    IF _CurrentOwnerEpoch IS NOT NULL THEN
        RETURN QUERY
        SELECT
            _ServiceId,
            _ProviderId,
            _QueueId,
            _CurrentOwnerEpoch,
            _CurrentCheckpoint,
            _Updated;
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION CleanupStreamMessages
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _RetentionPeriodSeconds INT,
    _MaximumRetentionPeriodSeconds INT,
    _CleanupIntervalSeconds INT,
    _CleanupBatchSize INT
)
RETURNS TABLE
(
    Ran BOOLEAN,
    DeletedCount INT,
    DeletedThroughMessageId BIGINT,
    HardDeletedCount INT,
    HardDeletedFromMessageId BIGINT,
    HardDeletedThroughMessageId BIGINT,
    Checkpoint BIGINT,
    EarliestMessageId BIGINT,
    TailMessageId BIGINT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMP(6) WITHOUT TIME ZONE := CURRENT_TIMESTAMP AT TIME ZONE 'UTC';
    _Checkpoint BIGINT;
    _DeletedCount INT := 0;
    _DeletedThroughMessageId BIGINT;
    _HardDeletedCount INT := 0;
    _HardDeletedFromMessageId BIGINT;
    _HardDeletedThroughMessageId BIGINT;
    _EarliestMessageId BIGINT;
    _TailMessageId BIGINT;
BEGIN
    UPDATE OrleansStreamPartition AS P
    SET
        CleanupOn = _Now + make_interval(secs => _CleanupIntervalSeconds),
        ModifiedOn = _Now
    WHERE P.ServiceId = _ServiceId
        AND P.ProviderId = _ProviderId
        AND P.QueueId = _QueueId
        AND P.CleanupOn <= _Now
    RETURNING P.Checkpoint INTO _Checkpoint;

    IF NOT FOUND THEN
        SELECT P.Checkpoint
        INTO _Checkpoint
        FROM OrleansStreamPartition AS P
        WHERE P.ServiceId = _ServiceId
            AND P.ProviderId = _ProviderId
            AND P.QueueId = _QueueId;

        SELECT MIN(M.MessageId), MAX(M.MessageId)
        INTO _EarliestMessageId, _TailMessageId
        FROM OrleansStreamMessage AS M
        WHERE M.ServiceId = _ServiceId
            AND M.ProviderId = _ProviderId
            AND M.QueueId = _QueueId;

        RETURN QUERY
        SELECT
            FALSE,
            0,
            NULL::BIGINT,
            0,
            NULL::BIGINT,
            NULL::BIGINT,
            _Checkpoint,
            _EarliestMessageId,
            _TailMessageId;
        RETURN;
    END IF;

    WITH Candidate AS
    (
        SELECT M.MessageId
        FROM OrleansStreamMessage AS M
        WHERE M.ServiceId = _ServiceId
            AND M.ProviderId = _ProviderId
            AND M.QueueId = _QueueId
            AND
            (
                (
                    _Checkpoint IS NOT NULL
                    AND M.MessageId <= _Checkpoint
                    AND M.CheckpointedOn < _Now - make_interval(secs => _RetentionPeriodSeconds)
                )
                OR
                (
                    _MaximumRetentionPeriodSeconds IS NOT NULL
                    AND M.CreatedOn < _Now - make_interval(secs => _MaximumRetentionPeriodSeconds)
                )
            )
        ORDER BY M.MessageId
        FOR UPDATE SKIP LOCKED
        LIMIT _CleanupBatchSize
    ),
    Deleted AS
    (
        DELETE FROM OrleansStreamMessage AS M
        USING Candidate AS C
        WHERE M.ServiceId = _ServiceId
            AND M.ProviderId = _ProviderId
            AND M.QueueId = _QueueId
            AND M.MessageId = C.MessageId
        RETURNING M.MessageId
    )
    SELECT
        COUNT(*)::INT,
        MAX(D.MessageId),
        COUNT(*) FILTER (WHERE _Checkpoint IS NULL OR D.MessageId > _Checkpoint)::INT,
        MIN(D.MessageId) FILTER (WHERE _Checkpoint IS NULL OR D.MessageId > _Checkpoint),
        MAX(D.MessageId) FILTER (WHERE _Checkpoint IS NULL OR D.MessageId > _Checkpoint)
    INTO
        _DeletedCount,
        _DeletedThroughMessageId,
        _HardDeletedCount,
        _HardDeletedFromMessageId,
        _HardDeletedThroughMessageId
    FROM Deleted AS D;

    SELECT MIN(M.MessageId), MAX(M.MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId
        AND M.ProviderId = _ProviderId
        AND M.QueueId = _QueueId;

    RETURN QUERY
    SELECT
        TRUE,
        _DeletedCount,
        _DeletedThroughMessageId,
        _HardDeletedCount,
        _HardDeletedFromMessageId,
        _HardDeletedThroughMessageId,
        _Checkpoint,
        _EarliestMessageId,
        _TailMessageId;
END;
$$;

INSERT INTO OrleansQuery (QueryKey, QueryText)
VALUES
    ('StreamSchemaVersionKey', '2'),
    ('AppendStreamMessageKey', 'SELECT * FROM AppendStreamMessage(@ServiceId, @ProviderId, @QueueId, @StreamIdBytes, @StreamNamespaceLength, @Payload)'),
    ('AcquireStreamPartitionKey', 'SELECT * FROM AcquireStreamPartition(@ServiceId, @ProviderId, @QueueId, @StartFromNow)'),
    ('ReadStreamMessagesKey', 'SELECT ServiceId, ProviderId, QueueId, MessageId, StreamIdBytes, StreamNamespaceLength, CreatedOn, Payload FROM OrleansStreamMessage WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId AND MessageId > @AfterMessageId ORDER BY MessageId LIMIT @MaxCount'),
    ('AdvanceStreamCheckpointKey', 'SELECT * FROM AdvanceStreamCheckpoint(@ServiceId, @ProviderId, @QueueId, @OwnerEpoch, @Checkpoint)'),
    ('GetStreamPartitionBoundsKey', 'SELECT P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.Checkpoint, MIN(M.MessageId) AS EarliestMessageId, MAX(M.MessageId) AS TailMessageId FROM OrleansStreamPartition AS P LEFT JOIN OrleansStreamMessage AS M ON M.ServiceId = P.ServiceId AND M.ProviderId = P.ProviderId AND M.QueueId = P.QueueId WHERE P.ServiceId = @ServiceId AND P.ProviderId = @ProviderId AND P.QueueId = @QueueId GROUP BY P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.Checkpoint'),
    ('CleanupStreamMessagesKey', 'SELECT * FROM CleanupStreamMessages(@ServiceId, @ProviderId, @QueueId, @RetentionPeriodSeconds, @MaximumRetentionPeriodSeconds, @CleanupIntervalSeconds, @CleanupBatchSize)');
