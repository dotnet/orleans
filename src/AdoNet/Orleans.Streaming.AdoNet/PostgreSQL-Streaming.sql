/*
ADO.NET streaming schema version 3.

This alpha schema is intentionally incompatible with the former destructive queue schema.
Drop the former streaming tables, sequence, routines, and OrleansQuery rows before applying
this script. Existing queue rows are not migrated.
*/

DO $$
BEGIN
    IF to_regclass('orleansstreampartition') IS NOT NULL
        OR to_regclass('orleansstreammessage') IS NOT NULL
        OR to_regclass('orleansstreamreplaylease') IS NOT NULL
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
        RAISE EXCEPTION 'Incompatible alpha ADO.NET streaming schema. Drop old streaming tables, sequence, routines, and OrleansQuery rows before applying version 3; no in-place migration is supported.';
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

CREATE TABLE OrleansStreamReplayLease
(
    ServiceId VARCHAR(150) NOT NULL,
    ProviderId VARCHAR(150) NOT NULL,
    QueueId VARCHAR(150) NOT NULL,
    ReaderId VARCHAR(150) NOT NULL,
    StreamIdBytes BYTEA NOT NULL,
    StreamNamespaceLength INT NOT NULL,
    OwnerEpoch BIGINT NOT NULL,
    Watermark BIGINT NOT NULL,
    ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
    CreatedOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,
    ModifiedOn TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL,

    CONSTRAINT PK_OrleansStreamReplayLease PRIMARY KEY
    (
        ServiceId,
        ProviderId,
        QueueId,
        ReaderId
    )
);

CREATE INDEX IX_OrleansStreamReplayLease_Active
    ON OrleansStreamReplayLease (ServiceId, ProviderId, QueueId, ExpiresOn, Watermark);

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
    _Now TIMESTAMP(6) WITHOUT TIME ZONE;
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
        clock_timestamp() AT TIME ZONE 'UTC',
        clock_timestamp() AT TIME ZONE 'UTC',
        clock_timestamp() AT TIME ZONE 'UTC'
    )
    ON CONFLICT (ServiceId, ProviderId, QueueId) DO NOTHING;

    UPDATE OrleansStreamPartition AS P
    SET
        NextMessageId = P.NextMessageId + 1,
        ModifiedOn = clock_timestamp() AT TIME ZONE 'UTC'
    WHERE P.ServiceId = _ServiceId
        AND P.ProviderId = _ProviderId
        AND P.QueueId = _QueueId
    RETURNING P.NextMessageId - 1 INTO _MessageId;

    _Now := clock_timestamp() AT TIME ZONE 'UTC';

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
    NextMessageId BIGINT,
    Checkpoint BIGINT,
    EarliestMessageId BIGINT,
    TailMessageId BIGINT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMP(6) WITHOUT TIME ZONE;
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
        clock_timestamp() AT TIME ZONE 'UTC',
        clock_timestamp() AT TIME ZONE 'UTC',
        clock_timestamp() AT TIME ZONE 'UTC'
    )
    ON CONFLICT (ServiceId, ProviderId, QueueId) DO NOTHING;

    SELECT P.NextMessageId, P.Checkpoint
    INTO _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition AS P
    WHERE P.ServiceId = _ServiceId
        AND P.ProviderId = _ProviderId
        AND P.QueueId = _QueueId
    FOR UPDATE;

    _Now := clock_timestamp() AT TIME ZONE 'UTC';

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
        AND M.MessageId <= _Checkpoint
        AND M.CheckpointedOn IS NULL;

    RETURN QUERY
    SELECT
        _ServiceId,
        _ProviderId,
        _QueueId,
        _OwnerEpoch,
        _NextMessageId,
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
    _Now TIMESTAMP(6) WITHOUT TIME ZONE;
    _CurrentOwnerEpoch BIGINT;
    _CurrentCheckpoint BIGINT;
    _PreviousCheckpoint BIGINT;
    _Updated BOOLEAN := FALSE;
BEGIN
    SELECT P.OwnerEpoch, P.Checkpoint
    INTO _CurrentOwnerEpoch, _CurrentCheckpoint
    FROM OrleansStreamPartition AS P
    WHERE P.ServiceId = _ServiceId
        AND P.ProviderId = _ProviderId
        AND P.QueueId = _QueueId
    FOR UPDATE;
    _PreviousCheckpoint := _CurrentCheckpoint;

    _Now := clock_timestamp() AT TIME ZONE 'UTC';

    UPDATE OrleansStreamPartition AS P
    SET
        Checkpoint = _Checkpoint,
        ModifiedOn = _Now
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
        SET CheckpointedOn = COALESCE(M.CheckpointedOn, _Now)
        WHERE M.ServiceId = _ServiceId
            AND M.ProviderId = _ProviderId
            AND M.QueueId = _QueueId
            AND (_PreviousCheckpoint IS NULL OR M.MessageId > _PreviousCheckpoint)
            AND M.MessageId <= _Checkpoint
            AND M.CheckpointedOn IS NULL;
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

CREATE OR REPLACE FUNCTION AcquireStreamReplayLease
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _ReaderId VARCHAR(150),
    _StreamIdBytes BYTEA,
    _StreamNamespaceLength INT,
    _OwnerEpoch BIGINT,
    _AfterMessageId BIGINT,
    _ReplayLeaseDurationSeconds INT
)
RETURNS TABLE
(
    Status VARCHAR(32),
    ServiceId VARCHAR(150),
    ProviderId VARCHAR(150),
    QueueId VARCHAR(150),
    ReaderId VARCHAR(150),
    OwnerEpoch BIGINT,
    Watermark BIGINT,
    ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE,
    NextMessageId BIGINT,
    Checkpoint BIGINT,
    EarliestMessageId BIGINT,
    TailMessageId BIGINT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMP(6) WITHOUT TIME ZONE;
    _CurrentOwnerEpoch BIGINT;
    _NextMessageId BIGINT;
    _Checkpoint BIGINT;
    _EarliestMessageId BIGINT;
    _TailMessageId BIGINT;
    _LeaseOwnerEpoch BIGINT;
    _Watermark BIGINT;
    _ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE;
    _Status VARCHAR(32);
BEGIN
    SELECT P.OwnerEpoch, P.NextMessageId, P.Checkpoint
    INTO _CurrentOwnerEpoch, _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition AS P
    WHERE P.ServiceId = _ServiceId AND P.ProviderId = _ProviderId AND P.QueueId = _QueueId
    FOR UPDATE;

    _Now := clock_timestamp() AT TIME ZONE 'UTC';

    SELECT L.OwnerEpoch, L.Watermark, L.ExpiresOn
    INTO _LeaseOwnerEpoch, _Watermark, _ExpiresOn
    FROM OrleansStreamReplayLease AS L
    WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
        AND L.QueueId = _QueueId AND L.ReaderId = _ReaderId
    FOR UPDATE;

    PERFORM M.MessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId AND M.QueueId = _QueueId
    FOR UPDATE;

    SELECT MIN(M.MessageId), MAX(M.MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId AND M.QueueId = _QueueId;

    IF _CurrentOwnerEpoch IS NULL OR _CurrentOwnerEpoch <> _OwnerEpoch
        OR (_LeaseOwnerEpoch IS NOT NULL AND _LeaseOwnerEpoch <> _OwnerEpoch AND _ExpiresOn > _Now)
    THEN
        _Status := 'OwnershipLost';
    ELSIF _AfterMessageId < COALESCE(_EarliestMessageId, _NextMessageId) - 1 THEN
        _Status := 'HistoryUnavailable';
    ELSE
        IF _LeaseOwnerEpoch IS NOT NULL
            AND (_LeaseOwnerEpoch <> _OwnerEpoch OR _ExpiresOn <= _Now)
        THEN
            DELETE FROM OrleansStreamReplayLease AS L
            WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
                AND L.QueueId = _QueueId AND L.ReaderId = _ReaderId;
            _LeaseOwnerEpoch := NULL;
        END IF;

        _ExpiresOn := _Now + make_interval(secs => _ReplayLeaseDurationSeconds);
        IF _LeaseOwnerEpoch IS NULL THEN
            _Watermark := _AfterMessageId;
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
            _Watermark := GREATEST(_Watermark, _AfterMessageId);
            UPDATE OrleansStreamReplayLease AS L
            SET Watermark = _Watermark, ExpiresOn = _ExpiresOn, ModifiedOn = _Now
            WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
                AND L.QueueId = _QueueId AND L.ReaderId = _ReaderId
                AND L.OwnerEpoch = _OwnerEpoch;
        END IF;
        _Status := 'Acquired';
    END IF;

    RETURN QUERY SELECT _Status, _ServiceId, _ProviderId, _QueueId, _ReaderId,
        _CurrentOwnerEpoch, _Watermark, _ExpiresOn, _NextMessageId, _Checkpoint,
        _EarliestMessageId, _TailMessageId;
END;
$$;

CREATE OR REPLACE FUNCTION ReadStreamReplayMessages
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _ReaderId VARCHAR(150),
    _OwnerEpoch BIGINT,
    _AfterMessageId BIGINT,
    _MaxCount INT,
    _ReplayLeaseDurationSeconds INT
)
RETURNS TABLE
(
    Status VARCHAR(32),
    OwnerEpoch BIGINT,
    Watermark BIGINT,
    ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE,
    NextMessageId BIGINT,
    Checkpoint BIGINT,
    EarliestMessageId BIGINT,
    TailMessageId BIGINT,
    MessageId BIGINT,
    StreamIdBytes BYTEA,
    StreamNamespaceLength INT,
    CreatedOn TIMESTAMP(6) WITHOUT TIME ZONE,
    Payload BYTEA
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMP(6) WITHOUT TIME ZONE;
    _CurrentOwnerEpoch BIGINT;
    _NextMessageId BIGINT;
    _Checkpoint BIGINT;
    _EarliestMessageId BIGINT;
    _TailMessageId BIGINT;
    _LeaseOwnerEpoch BIGINT;
    _Watermark BIGINT;
    _ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE;
    _Status VARCHAR(32);
BEGIN
    SELECT P.OwnerEpoch, P.NextMessageId, P.Checkpoint
    INTO _CurrentOwnerEpoch, _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition AS P
    WHERE P.ServiceId = _ServiceId AND P.ProviderId = _ProviderId AND P.QueueId = _QueueId
    FOR UPDATE;

    _Now := clock_timestamp() AT TIME ZONE 'UTC';

    SELECT L.OwnerEpoch, L.Watermark, L.ExpiresOn
    INTO _LeaseOwnerEpoch, _Watermark, _ExpiresOn
    FROM OrleansStreamReplayLease AS L
    WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
        AND L.QueueId = _QueueId AND L.ReaderId = _ReaderId
    FOR UPDATE;

    PERFORM M.MessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId AND M.QueueId = _QueueId
    FOR UPDATE;

    SELECT MIN(M.MessageId), MAX(M.MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId AND M.QueueId = _QueueId;

    IF _CurrentOwnerEpoch IS NULL OR _CurrentOwnerEpoch <> _OwnerEpoch
        OR _LeaseOwnerEpoch IS NULL OR _LeaseOwnerEpoch <> _OwnerEpoch
    THEN
        _Status := 'OwnershipLost';
    ELSIF _ExpiresOn <= _Now THEN
        _Status := 'Expired';
    ELSIF _AfterMessageId < COALESCE(_EarliestMessageId, _NextMessageId) - 1 THEN
        _Status := 'HistoryUnavailable';
    ELSE
        _ExpiresOn := _Now + make_interval(secs => _ReplayLeaseDurationSeconds);
        UPDATE OrleansStreamReplayLease AS L
        SET ExpiresOn = _ExpiresOn, ModifiedOn = _Now
        WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
            AND L.QueueId = _QueueId AND L.ReaderId = _ReaderId AND L.OwnerEpoch = _OwnerEpoch;
        _Status := 'Active';
    END IF;

    IF _Status = 'Active' THEN
        RETURN QUERY
        SELECT _Status, _CurrentOwnerEpoch, _Watermark, _ExpiresOn, _NextMessageId,
            _Checkpoint, _EarliestMessageId, _TailMessageId, M.MessageId,
            M.StreamIdBytes, M.StreamNamespaceLength, M.CreatedOn, M.Payload
        FROM OrleansStreamMessage AS M
        WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId
            AND M.QueueId = _QueueId AND M.MessageId > _AfterMessageId
        ORDER BY M.MessageId
        LIMIT _MaxCount;
    END IF;

    IF _Status <> 'Active' OR NOT FOUND THEN
        RETURN QUERY SELECT _Status, _CurrentOwnerEpoch, _Watermark, _ExpiresOn,
            _NextMessageId, _Checkpoint, _EarliestMessageId, _TailMessageId,
            NULL::BIGINT, NULL::BYTEA, NULL::INT,
            NULL::TIMESTAMP(6) WITHOUT TIME ZONE, NULL::BYTEA;
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION UpdateStreamReplayLease
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _ReaderId VARCHAR(150),
    _OwnerEpoch BIGINT,
    _Watermark BIGINT,
    _ReplayLeaseDurationSeconds INT
)
RETURNS TABLE
(
    Status VARCHAR(32), OwnerEpoch BIGINT, Watermark BIGINT,
    ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE, NextMessageId BIGINT,
    Checkpoint BIGINT, EarliestMessageId BIGINT, TailMessageId BIGINT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMP(6) WITHOUT TIME ZONE;
    _CurrentOwnerEpoch BIGINT;
    _NextMessageId BIGINT;
    _Checkpoint BIGINT;
    _EarliestMessageId BIGINT;
    _TailMessageId BIGINT;
    _LeaseOwnerEpoch BIGINT;
    _CurrentWatermark BIGINT;
    _ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE;
    _Status VARCHAR(32);
BEGIN
    SELECT P.OwnerEpoch, P.NextMessageId, P.Checkpoint
    INTO _CurrentOwnerEpoch, _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition AS P
    WHERE P.ServiceId = _ServiceId AND P.ProviderId = _ProviderId AND P.QueueId = _QueueId
    FOR UPDATE;
    _Now := clock_timestamp() AT TIME ZONE 'UTC';

    SELECT L.OwnerEpoch, L.Watermark, L.ExpiresOn
    INTO _LeaseOwnerEpoch, _CurrentWatermark, _ExpiresOn
    FROM OrleansStreamReplayLease AS L
    WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
        AND L.QueueId = _QueueId AND L.ReaderId = _ReaderId
    FOR UPDATE;

    PERFORM M.MessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId AND M.QueueId = _QueueId
    FOR UPDATE;

    SELECT MIN(M.MessageId), MAX(M.MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId AND M.QueueId = _QueueId;

    IF _CurrentOwnerEpoch IS NULL OR _CurrentOwnerEpoch <> _OwnerEpoch
        OR _LeaseOwnerEpoch IS NULL OR _LeaseOwnerEpoch <> _OwnerEpoch
    THEN
        _Status := 'OwnershipLost';
    ELSIF _ExpiresOn <= _Now THEN
        _Status := 'Expired';
    ELSIF _Watermark < COALESCE(_EarliestMessageId, _NextMessageId) - 1 THEN
        _Status := 'HistoryUnavailable';
    ELSE
        _CurrentWatermark := GREATEST(_CurrentWatermark, _Watermark);
        _ExpiresOn := _Now + make_interval(secs => _ReplayLeaseDurationSeconds);
        UPDATE OrleansStreamReplayLease AS L
        SET Watermark = _CurrentWatermark, ExpiresOn = _ExpiresOn, ModifiedOn = _Now
        WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
            AND L.QueueId = _QueueId AND L.ReaderId = _ReaderId AND L.OwnerEpoch = _OwnerEpoch;
        _Status := 'Active';
    END IF;

    RETURN QUERY SELECT _Status, _CurrentOwnerEpoch, _CurrentWatermark, _ExpiresOn,
        _NextMessageId, _Checkpoint, _EarliestMessageId, _TailMessageId;
END;
$$;

CREATE OR REPLACE FUNCTION ReleaseStreamReplayLease
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _ReaderId VARCHAR(150),
    _OwnerEpoch BIGINT
)
RETURNS TABLE
(
    Status VARCHAR(32), OwnerEpoch BIGINT, Watermark BIGINT,
    ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE, NextMessageId BIGINT,
    Checkpoint BIGINT, EarliestMessageId BIGINT, TailMessageId BIGINT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _CurrentOwnerEpoch BIGINT;
    _NextMessageId BIGINT;
    _Checkpoint BIGINT;
    _EarliestMessageId BIGINT;
    _TailMessageId BIGINT;
    _LeaseOwnerEpoch BIGINT;
    _Watermark BIGINT;
    _ExpiresOn TIMESTAMP(6) WITHOUT TIME ZONE;
    _Status VARCHAR(32);
BEGIN
    SELECT P.OwnerEpoch, P.NextMessageId, P.Checkpoint
    INTO _CurrentOwnerEpoch, _NextMessageId, _Checkpoint
    FROM OrleansStreamPartition AS P
    WHERE P.ServiceId = _ServiceId AND P.ProviderId = _ProviderId AND P.QueueId = _QueueId
    FOR UPDATE;

    SELECT L.OwnerEpoch, L.Watermark, L.ExpiresOn
    INTO _LeaseOwnerEpoch, _Watermark, _ExpiresOn
    FROM OrleansStreamReplayLease AS L
    WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
        AND L.QueueId = _QueueId AND L.ReaderId = _ReaderId
    FOR UPDATE;

    PERFORM M.MessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId AND M.QueueId = _QueueId
    FOR UPDATE;

    SELECT MIN(M.MessageId), MAX(M.MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId AND M.QueueId = _QueueId;

    IF _CurrentOwnerEpoch IS NULL OR _CurrentOwnerEpoch <> _OwnerEpoch
        OR (_LeaseOwnerEpoch IS NOT NULL AND _LeaseOwnerEpoch <> _OwnerEpoch)
    THEN
        _Status := 'OwnershipLost';
    ELSE
        DELETE FROM OrleansStreamReplayLease AS L
        WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
            AND L.QueueId = _QueueId AND L.ReaderId = _ReaderId AND L.OwnerEpoch = _OwnerEpoch;
        _Status := 'Released';
    END IF;

    RETURN QUERY SELECT _Status, _CurrentOwnerEpoch, _Watermark, _ExpiresOn,
        _NextMessageId, _Checkpoint, _EarliestMessageId, _TailMessageId;
END;
$$;

CREATE OR REPLACE FUNCTION CleanupStreamMessages
(
    _ServiceId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _QueueId VARCHAR(150),
    _OwnerEpoch BIGINT,
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
    ActiveReplayWatermark BIGINT,
    EarliestMessageId BIGINT,
    TailMessageId BIGINT
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMP(6) WITHOUT TIME ZONE;
    _CurrentOwnerEpoch BIGINT;
    _Checkpoint BIGINT;
    _CleanupOn TIMESTAMP(6) WITHOUT TIME ZONE;
    _ActiveReplayWatermark BIGINT;
    _DeletedCount INT := 0;
    _DeletedThroughMessageId BIGINT;
    _HardDeletedCount INT := 0;
    _HardDeletedFromMessageId BIGINT;
    _HardDeletedThroughMessageId BIGINT;
    _EarliestMessageId BIGINT;
    _TailMessageId BIGINT;
BEGIN
    SELECT P.OwnerEpoch, P.Checkpoint, P.CleanupOn
    INTO _CurrentOwnerEpoch, _Checkpoint, _CleanupOn
    FROM OrleansStreamPartition AS P
    WHERE P.ServiceId = _ServiceId AND P.ProviderId = _ProviderId AND P.QueueId = _QueueId
    FOR UPDATE;

    _Now := clock_timestamp() AT TIME ZONE 'UTC';

    PERFORM L.ReaderId
    FROM OrleansStreamReplayLease AS L
    WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId AND L.QueueId = _QueueId
    FOR UPDATE;

    IF _CurrentOwnerEpoch = _OwnerEpoch THEN
        DELETE FROM OrleansStreamReplayLease AS L
        WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
            AND L.QueueId = _QueueId AND L.ExpiresOn <= _Now;
    END IF;

    SELECT MIN(L.Watermark)
    INTO _ActiveReplayWatermark
    FROM OrleansStreamReplayLease AS L
    WHERE L.ServiceId = _ServiceId AND L.ProviderId = _ProviderId
        AND L.QueueId = _QueueId AND L.ExpiresOn > _Now;

    PERFORM M.MessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId AND M.QueueId = _QueueId
    FOR UPDATE;

    SELECT MIN(M.MessageId), MAX(M.MessageId)
    INTO _EarliestMessageId, _TailMessageId
    FROM OrleansStreamMessage AS M
    WHERE M.ServiceId = _ServiceId AND M.ProviderId = _ProviderId AND M.QueueId = _QueueId;

    IF _CurrentOwnerEpoch IS NULL OR _CurrentOwnerEpoch <> _OwnerEpoch OR _CleanupOn > _Now THEN

        RETURN QUERY
        SELECT
            FALSE,
            0,
            NULL::BIGINT,
            0,
            NULL::BIGINT,
            NULL::BIGINT,
            _Checkpoint,
            _ActiveReplayWatermark,
            _EarliestMessageId,
            _TailMessageId;
        RETURN;
    END IF;

    UPDATE OrleansStreamPartition AS P
    SET CleanupOn = _Now + make_interval(secs => _CleanupIntervalSeconds), ModifiedOn = _Now
    WHERE P.ServiceId = _ServiceId AND P.ProviderId = _ProviderId
        AND P.QueueId = _QueueId AND P.OwnerEpoch = _OwnerEpoch;

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
                    AND (_ActiveReplayWatermark IS NULL OR M.MessageId <= _ActiveReplayWatermark)
                )
                OR
                (
                    _MaximumRetentionPeriodSeconds IS NOT NULL
                    AND M.CreatedOn < _Now - make_interval(secs => _MaximumRetentionPeriodSeconds)
                )
            )
        ORDER BY M.MessageId
            LIMIT _CleanupBatchSize
            FOR UPDATE
    ),
    Deleted AS
    (
        DELETE FROM OrleansStreamMessage AS M
        USING Candidate AS C
        WHERE M.ServiceId = _ServiceId
            AND M.ProviderId = _ProviderId
            AND M.QueueId = _QueueId
            AND M.MessageId = C.MessageId
        RETURNING M.MessageId, M.CreatedOn, M.CheckpointedOn
    )
    SELECT
        COUNT(*)::INT,
        MAX(D.MessageId),
        COUNT(*) FILTER
        (
            WHERE _MaximumRetentionPeriodSeconds IS NOT NULL
                AND D.CreatedOn < _Now - make_interval(secs => _MaximumRetentionPeriodSeconds)
                AND NOT
                (
                    _Checkpoint IS NOT NULL
                    AND D.MessageId <= _Checkpoint
                    AND D.CheckpointedOn < _Now - make_interval(secs => _RetentionPeriodSeconds)
                    AND (_ActiveReplayWatermark IS NULL OR D.MessageId <= _ActiveReplayWatermark)
                )
        )::INT,
        MIN(D.MessageId) FILTER
        (
            WHERE _MaximumRetentionPeriodSeconds IS NOT NULL
                AND D.CreatedOn < _Now - make_interval(secs => _MaximumRetentionPeriodSeconds)
                AND NOT
                (
                    _Checkpoint IS NOT NULL
                    AND D.MessageId <= _Checkpoint
                    AND D.CheckpointedOn < _Now - make_interval(secs => _RetentionPeriodSeconds)
                    AND (_ActiveReplayWatermark IS NULL OR D.MessageId <= _ActiveReplayWatermark)
                )
        ),
        MAX(D.MessageId) FILTER
        (
            WHERE _MaximumRetentionPeriodSeconds IS NOT NULL
                AND D.CreatedOn < _Now - make_interval(secs => _MaximumRetentionPeriodSeconds)
                AND NOT
                (
                    _Checkpoint IS NOT NULL
                    AND D.MessageId <= _Checkpoint
                    AND D.CheckpointedOn < _Now - make_interval(secs => _RetentionPeriodSeconds)
                    AND (_ActiveReplayWatermark IS NULL OR D.MessageId <= _ActiveReplayWatermark)
                )
        )
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
        _ActiveReplayWatermark,
        _EarliestMessageId,
        _TailMessageId;
END;
$$;

INSERT INTO OrleansQuery (QueryKey, QueryText)
VALUES
    ('StreamSchemaVersionKey', '3'),
    ('AppendStreamMessageKey', 'SELECT * FROM AppendStreamMessage(@ServiceId, @ProviderId, @QueueId, @StreamIdBytes, @StreamNamespaceLength, @Payload)'),
    ('AcquireStreamPartitionKey', 'SELECT * FROM AcquireStreamPartition(@ServiceId, @ProviderId, @QueueId, @StartFromNow)'),
    ('ReadStreamMessagesKey', 'SELECT ServiceId, ProviderId, QueueId, MessageId, StreamIdBytes, StreamNamespaceLength, CreatedOn, Payload FROM OrleansStreamMessage WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId AND MessageId > @AfterMessageId ORDER BY MessageId LIMIT @MaxCount'),
    ('AdvanceStreamCheckpointKey', 'SELECT * FROM AdvanceStreamCheckpoint(@ServiceId, @ProviderId, @QueueId, @OwnerEpoch, @Checkpoint)'),
    ('GetStreamPartitionBoundsKey', 'SELECT P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.NextMessageId, P.Checkpoint, MIN(M.MessageId) AS EarliestMessageId, MAX(M.MessageId) AS TailMessageId FROM OrleansStreamPartition AS P LEFT JOIN OrleansStreamMessage AS M ON M.ServiceId = P.ServiceId AND M.ProviderId = P.ProviderId AND M.QueueId = P.QueueId WHERE P.ServiceId = @ServiceId AND P.ProviderId = @ProviderId AND P.QueueId = @QueueId GROUP BY P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.NextMessageId, P.Checkpoint'),
    ('AcquireStreamReplayLeaseKey', 'SELECT * FROM AcquireStreamReplayLease(@ServiceId, @ProviderId, @QueueId, @ReaderId, @StreamIdBytes, @StreamNamespaceLength, @OwnerEpoch, @AfterMessageId, @ReplayLeaseDurationSeconds)'),
    ('ReadStreamReplayMessagesKey', 'SELECT * FROM ReadStreamReplayMessages(@ServiceId, @ProviderId, @QueueId, @ReaderId, @OwnerEpoch, @AfterMessageId, @MaxCount, @ReplayLeaseDurationSeconds)'),
    ('UpdateStreamReplayLeaseKey', 'SELECT * FROM UpdateStreamReplayLease(@ServiceId, @ProviderId, @QueueId, @ReaderId, @OwnerEpoch, @Watermark, @ReplayLeaseDurationSeconds)'),
    ('ReleaseStreamReplayLeaseKey', 'SELECT * FROM ReleaseStreamReplayLease(@ServiceId, @ProviderId, @QueueId, @ReaderId, @OwnerEpoch)'),
    ('CleanupStreamMessagesKey', 'SELECT * FROM CleanupStreamMessages(@ServiceId, @ProviderId, @QueueId, @OwnerEpoch, @RetentionPeriodSeconds, @MaximumRetentionPeriodSeconds, @CleanupIntervalSeconds, @CleanupBatchSize)');
