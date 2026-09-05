/*
ADO.NET streaming schema version 3.

This alpha schema is intentionally incompatible with the former destructive queue schema.
Drop the former streaming tables, sequence, routines, and OrleansQuery rows before applying
this script. Existing queue rows are not migrated.
*/

IF OBJECT_ID(N'OrleansStreamPartition', N'U') IS NOT NULL
    OR OBJECT_ID(N'OrleansStreamMessage', N'U') IS NOT NULL
    OR OBJECT_ID(N'OrleansStreamReplayLease', N'U') IS NOT NULL
    OR OBJECT_ID(N'OrleansStreamDeadLetter', N'U') IS NOT NULL
    OR OBJECT_ID(N'OrleansStreamControl', N'U') IS NOT NULL
    OR OBJECT_ID(N'OrleansStreamMessageSequence', N'SO') IS NOT NULL
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
BEGIN
    THROW 51001, 'Incompatible alpha ADO.NET streaming schema. Drop old streaming tables, sequence, routines, and OrleansQuery rows before applying version 3; no in-place migration is supported.', 1;
END;
GO

CREATE TABLE OrleansStreamPartition
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    NextMessageId BIGINT NOT NULL,
    [Checkpoint] BIGINT NULL,
    OwnerEpoch BIGINT NOT NULL,
    CleanupOn DATETIME2(7) NOT NULL,
    CreatedOn DATETIME2(7) NOT NULL,
    ModifiedOn DATETIME2(7) NOT NULL,

    CONSTRAINT PK_OrleansStreamPartition PRIMARY KEY CLUSTERED
    (
        ServiceId,
        ProviderId,
        QueueId
    )
);
GO

CREATE TABLE OrleansStreamMessage
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    MessageId BIGINT NOT NULL,
    StreamIdBytes VARBINARY(MAX) NOT NULL,
    StreamNamespaceLength INT NOT NULL,
    CreatedOn DATETIME2(7) NOT NULL,
    CheckpointedOn DATETIME2(7) NULL,
    Payload VARBINARY(MAX) NOT NULL,

    CONSTRAINT PK_OrleansStreamMessage PRIMARY KEY CLUSTERED
    (
        ServiceId,
        ProviderId,
        QueueId,
        MessageId
    )
);
GO

CREATE TABLE OrleansStreamReplayLease
(
    ServiceId NVARCHAR(150) NOT NULL,
    ProviderId NVARCHAR(150) NOT NULL,
    QueueId NVARCHAR(150) NOT NULL,
    ReaderId NVARCHAR(150) NOT NULL,
    StreamIdBytes VARBINARY(MAX) NOT NULL,
    StreamNamespaceLength INT NOT NULL,
    OwnerEpoch BIGINT NOT NULL,
    Watermark BIGINT NOT NULL,
    ExpiresOn DATETIME2(7) NOT NULL,
    CreatedOn DATETIME2(7) NOT NULL,
    ModifiedOn DATETIME2(7) NOT NULL,

    CONSTRAINT PK_OrleansStreamReplayLease PRIMARY KEY NONCLUSTERED
    (
        ServiceId,
        ProviderId,
        QueueId,
        ReaderId
    )
);
GO

CREATE INDEX IX_OrleansStreamReplayLease_Active
    ON OrleansStreamReplayLease (ServiceId, ProviderId, QueueId, ExpiresOn, Watermark);
GO

CREATE PROCEDURE AppendStreamMessage
    @ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @QueueId NVARCHAR(150),
    @StreamIdBytes VARBINARY(MAX),
    @StreamNamespaceLength INT,
    @Payload VARBINARY(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartedTransaction BIT = 0;
    DECLARE @Now DATETIME2(7);
    DECLARE @LockedNextMessageId BIGINT;
    DECLARE @Allocated TABLE (MessageId BIGINT NOT NULL);

    BEGIN TRY
        IF @@TRANCOUNT = 0
        BEGIN
            BEGIN TRANSACTION;
            SET @StartedTransaction = 1;
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM OrleansStreamPartition
            WHERE ServiceId = @ServiceId
                AND ProviderId = @ProviderId
                AND QueueId = @QueueId
        )
        BEGIN
            DECLARE @InitializationLockResource NVARCHAR(255) = CONCAT
            (
                N'OrleansStreamPartition:',
                CONVERT
                (
                    VARCHAR(64),
                    HASHBYTES
                    (
                        'SHA2_256',
                        CONCAT
                        (
                            DATALENGTH(@ServiceId), N':', @ServiceId,
                            DATALENGTH(@ProviderId), N':', @ProviderId,
                            DATALENGTH(@QueueId), N':', @QueueId
                        )
                    ),
                    2
                )
            );
            DECLARE @InitializationLockResult INT;

            EXECUTE @InitializationLockResult = sys.sp_getapplock
                @Resource = @InitializationLockResource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction';

            IF @InitializationLockResult < 0
            BEGIN
                THROW 51000, 'Failed to acquire the stream partition initialization lock.', 1;
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM OrleansStreamPartition
                WHERE ServiceId = @ServiceId
                    AND ProviderId = @ProviderId
                    AND QueueId = @QueueId
            )
            BEGIN
                INSERT INTO OrleansStreamPartition
                (
                    ServiceId,
                    ProviderId,
                    QueueId,
                    NextMessageId,
                    [Checkpoint],
                    OwnerEpoch,
                    CleanupOn,
                    CreatedOn,
                    ModifiedOn
                )
                VALUES
                (
                    @ServiceId,
                    @ProviderId,
                    @QueueId,
                    1,
                    NULL,
                    0,
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                );
            END;
        END;

        SELECT @LockedNextMessageId = NextMessageId
        FROM OrleansStreamPartition WITH (UPDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        SET @Now = SYSUTCDATETIME();

        UPDATE OrleansStreamPartition WITH (UPDLOCK, ROWLOCK)
        SET
            NextMessageId = NextMessageId + 1,
            ModifiedOn = @Now
        OUTPUT Inserted.NextMessageId - 1 INTO @Allocated (MessageId)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

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
        SELECT
            @ServiceId,
            @ProviderId,
            @QueueId,
            MessageId,
            @StreamIdBytes,
            @StreamNamespaceLength,
            @Now,
            @Payload
        FROM @Allocated;

        IF @StartedTransaction = 1
        BEGIN
            COMMIT TRANSACTION;
        END;

        SELECT
            @ServiceId AS ServiceId,
            @ProviderId AS ProviderId,
            @QueueId AS QueueId,
            MessageId
        FROM @Allocated;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE AcquireStreamPartition
    @ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @QueueId NVARCHAR(150),
    @StartFromNow BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartedTransaction BIT = 0;
    DECLARE @Now DATETIME2(7);
    DECLARE @NextMessageId BIGINT;
    DECLARE @Checkpoint BIGINT;
    DECLARE @OwnerEpoch BIGINT;
    DECLARE @EarliestMessageId BIGINT;
    DECLARE @TailMessageId BIGINT;

    BEGIN TRY
        IF @@TRANCOUNT = 0
        BEGIN
            BEGIN TRANSACTION;
            SET @StartedTransaction = 1;
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM OrleansStreamPartition
            WHERE ServiceId = @ServiceId
                AND ProviderId = @ProviderId
                AND QueueId = @QueueId
        )
        BEGIN
            DECLARE @InitializationLockResource NVARCHAR(255) = CONCAT
            (
                N'OrleansStreamPartition:',
                CONVERT
                (
                    VARCHAR(64),
                    HASHBYTES
                    (
                        'SHA2_256',
                        CONCAT
                        (
                            DATALENGTH(@ServiceId), N':', @ServiceId,
                            DATALENGTH(@ProviderId), N':', @ProviderId,
                            DATALENGTH(@QueueId), N':', @QueueId
                        )
                    ),
                    2
                )
            );
            DECLARE @InitializationLockResult INT;

            EXECUTE @InitializationLockResult = sys.sp_getapplock
                @Resource = @InitializationLockResource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction';

            IF @InitializationLockResult < 0
            BEGIN
                THROW 51000, 'Failed to acquire the stream partition initialization lock.', 1;
            END;

            IF NOT EXISTS
            (
                SELECT 1
                FROM OrleansStreamPartition
                WHERE ServiceId = @ServiceId
                    AND ProviderId = @ProviderId
                    AND QueueId = @QueueId
            )
            BEGIN
                INSERT INTO OrleansStreamPartition
                (
                    ServiceId,
                    ProviderId,
                    QueueId,
                    NextMessageId,
                    [Checkpoint],
                    OwnerEpoch,
                    CleanupOn,
                    CreatedOn,
                    ModifiedOn
                )
                VALUES
                (
                    @ServiceId,
                    @ProviderId,
                    @QueueId,
                    1,
                    NULL,
                    0,
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                );
            END;
        END;

        SELECT
            @NextMessageId = NextMessageId,
            @Checkpoint = [Checkpoint]
        FROM OrleansStreamPartition WITH (UPDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        SET @Now = SYSUTCDATETIME();

        SELECT
            @EarliestMessageId = MIN(MessageId),
            @TailMessageId = MAX(MessageId)
        FROM OrleansStreamMessage
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        IF @Checkpoint IS NULL
        BEGIN
            SET @Checkpoint = CASE
                WHEN @StartFromNow = 1 THEN @NextMessageId - 1
                ELSE COALESCE(@EarliestMessageId - 1, @NextMessageId - 1)
            END;
        END;

        UPDATE OrleansStreamPartition WITH (UPDLOCK, ROWLOCK)
        SET
            [Checkpoint] = @Checkpoint,
            OwnerEpoch = OwnerEpoch + 1,
            ModifiedOn = @Now
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        UPDATE OrleansStreamMessage
        SET CheckpointedOn = COALESCE(CheckpointedOn, @Now)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId
            AND MessageId <= @Checkpoint
            AND CheckpointedOn IS NULL;

        SELECT @OwnerEpoch = OwnerEpoch
        FROM OrleansStreamPartition
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        IF @StartedTransaction = 1
        BEGIN
            COMMIT TRANSACTION;
        END;

        SELECT
            @ServiceId AS ServiceId,
            @ProviderId AS ProviderId,
            @QueueId AS QueueId,
            @OwnerEpoch AS OwnerEpoch,
            @NextMessageId AS NextMessageId,
            @Checkpoint AS [Checkpoint],
            @EarliestMessageId AS EarliestMessageId,
            @TailMessageId AS TailMessageId;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE AdvanceStreamCheckpoint
    @ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @QueueId NVARCHAR(150),
    @OwnerEpoch BIGINT,
    @Checkpoint BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Result TABLE
    (
        ServiceId NVARCHAR(150) NOT NULL,
        ProviderId NVARCHAR(150) NOT NULL,
        QueueId NVARCHAR(150) NOT NULL,
        OwnerEpoch BIGINT NOT NULL,
        [Checkpoint] BIGINT NULL,
        Updated BIT NOT NULL
    );
    DECLARE @Now DATETIME2(7);
    DECLARE @LockedCheckpoint BIGINT;
    DECLARE @StartedTransaction BIT = 0;

    BEGIN TRY
        IF @@TRANCOUNT = 0
        BEGIN
            BEGIN TRANSACTION;
            SET @StartedTransaction = 1;
        END;

        SELECT @LockedCheckpoint = [Checkpoint]
        FROM OrleansStreamPartition WITH (UPDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        SET @Now = SYSUTCDATETIME();

        UPDATE OrleansStreamPartition WITH (UPDLOCK, ROWLOCK)
        SET
            [Checkpoint] = @Checkpoint,
            ModifiedOn = @Now
        OUTPUT
            Inserted.ServiceId,
            Inserted.ProviderId,
            Inserted.QueueId,
            Inserted.OwnerEpoch,
            Inserted.[Checkpoint],
            CAST(1 AS BIT)
        INTO @Result
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId
            AND OwnerEpoch = @OwnerEpoch
            AND ([Checkpoint] IS NULL OR [Checkpoint] < @Checkpoint)
            AND @Checkpoint < NextMessageId;

        IF EXISTS (SELECT 1 FROM @Result)
        BEGIN
            UPDATE OrleansStreamMessage
            SET CheckpointedOn = COALESCE(CheckpointedOn, @Now)
            WHERE ServiceId = @ServiceId
                AND ProviderId = @ProviderId
                AND QueueId = @QueueId
                AND (@LockedCheckpoint IS NULL OR MessageId > @LockedCheckpoint)
                AND MessageId <= @Checkpoint
                AND CheckpointedOn IS NULL;
        END;

        IF @StartedTransaction = 1
        BEGIN
            COMMIT TRANSACTION;
        END;

        IF EXISTS (SELECT 1 FROM @Result)
        BEGIN
            SELECT ServiceId, ProviderId, QueueId, OwnerEpoch, [Checkpoint], Updated
            FROM @Result;
            RETURN;
        END;

        SELECT
            ServiceId,
            ProviderId,
            QueueId,
            OwnerEpoch,
            [Checkpoint],
            CAST(0 AS BIT) AS Updated
        FROM OrleansStreamPartition
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;
        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE AcquireStreamReplayLease
    @ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @QueueId NVARCHAR(150),
    @ReaderId NVARCHAR(150),
    @StreamIdBytes VARBINARY(MAX),
    @StreamNamespaceLength INT,
    @OwnerEpoch BIGINT,
    @AfterMessageId BIGINT,
    @ReplayLeaseDurationSeconds INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartedTransaction BIT = 0;
    DECLARE @Now DATETIME2(7);
    DECLARE @CurrentOwnerEpoch BIGINT;
    DECLARE @NextMessageId BIGINT;
    DECLARE @Checkpoint BIGINT;
    DECLARE @EarliestMessageId BIGINT;
    DECLARE @TailMessageId BIGINT;
    DECLARE @LeaseOwnerEpoch BIGINT;
    DECLARE @Watermark BIGINT;
    DECLARE @ExpiresOn DATETIME2(7);
    DECLARE @Status VARCHAR(32);

    BEGIN TRY
        IF @@TRANCOUNT = 0
        BEGIN
            BEGIN TRANSACTION;
            SET @StartedTransaction = 1;
        END;

        SELECT
            @CurrentOwnerEpoch = OwnerEpoch,
            @NextMessageId = NextMessageId,
            @Checkpoint = [Checkpoint]
        FROM OrleansStreamPartition WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        SET @Now = SYSUTCDATETIME();

        SELECT
            @LeaseOwnerEpoch = OwnerEpoch,
            @Watermark = Watermark,
            @ExpiresOn = ExpiresOn
        FROM OrleansStreamReplayLease WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId
            AND ReaderId = @ReaderId;

        SELECT
            @EarliestMessageId = MIN(MessageId),
            @TailMessageId = MAX(MessageId)
        FROM OrleansStreamMessage WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        IF @CurrentOwnerEpoch IS NULL OR @CurrentOwnerEpoch <> @OwnerEpoch
            OR (@LeaseOwnerEpoch IS NOT NULL AND @LeaseOwnerEpoch <> @OwnerEpoch AND @ExpiresOn > @Now)
        BEGIN
            SET @Status = 'OwnershipLost';
        END
        ELSE IF @AfterMessageId < COALESCE(@EarliestMessageId, @NextMessageId) - 1
        BEGIN
            SET @Status = 'HistoryUnavailable';
        END
        ELSE
        BEGIN
            IF @LeaseOwnerEpoch IS NOT NULL AND (@LeaseOwnerEpoch <> @OwnerEpoch OR @ExpiresOn <= @Now)
            BEGIN
                DELETE FROM OrleansStreamReplayLease
                WHERE ServiceId = @ServiceId
                    AND ProviderId = @ProviderId
                    AND QueueId = @QueueId
                    AND ReaderId = @ReaderId;
                SET @LeaseOwnerEpoch = NULL;
            END;

            IF @LeaseOwnerEpoch IS NULL
            BEGIN
                SET @Watermark = @AfterMessageId;
                SET @ExpiresOn = DATEADD(SECOND, @ReplayLeaseDurationSeconds, @Now);
                INSERT INTO OrleansStreamReplayLease
                (
                    ServiceId, ProviderId, QueueId, ReaderId, StreamIdBytes,
                    StreamNamespaceLength, OwnerEpoch, Watermark, ExpiresOn, CreatedOn, ModifiedOn
                )
                VALUES
                (
                    @ServiceId, @ProviderId, @QueueId, @ReaderId, @StreamIdBytes,
                    @StreamNamespaceLength, @OwnerEpoch, @Watermark, @ExpiresOn, @Now, @Now
                );
            END
            ELSE
            BEGIN
                SET @Watermark = CASE WHEN @Watermark < @AfterMessageId THEN @AfterMessageId ELSE @Watermark END;
                SET @ExpiresOn = DATEADD(SECOND, @ReplayLeaseDurationSeconds, @Now);
                UPDATE OrleansStreamReplayLease
                SET
                    Watermark = @Watermark,
                    ExpiresOn = @ExpiresOn,
                    ModifiedOn = @Now
                WHERE ServiceId = @ServiceId
                    AND ProviderId = @ProviderId
                    AND QueueId = @QueueId
                    AND ReaderId = @ReaderId
                    AND OwnerEpoch = @OwnerEpoch;
            END;
            SET @Status = 'Acquired';
        END;

        IF @StartedTransaction = 1 COMMIT TRANSACTION;

        SELECT
            @Status AS Status,
            @ServiceId AS ServiceId,
            @ProviderId AS ProviderId,
            @QueueId AS QueueId,
            @ReaderId AS ReaderId,
            @CurrentOwnerEpoch AS OwnerEpoch,
            @Watermark AS Watermark,
            @ExpiresOn AS ExpiresOn,
            @NextMessageId AS NextMessageId,
            @Checkpoint AS [Checkpoint],
            @EarliestMessageId AS EarliestMessageId,
            @TailMessageId AS TailMessageId;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE ReadStreamReplayMessages
    @ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @QueueId NVARCHAR(150),
    @ReaderId NVARCHAR(150),
    @OwnerEpoch BIGINT,
    @AfterMessageId BIGINT,
    @MaxCount INT,
    @ReplayLeaseDurationSeconds INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartedTransaction BIT = 0;
    DECLARE @Now DATETIME2(7);
    DECLARE @CurrentOwnerEpoch BIGINT;
    DECLARE @NextMessageId BIGINT;
    DECLARE @Checkpoint BIGINT;
    DECLARE @EarliestMessageId BIGINT;
    DECLARE @TailMessageId BIGINT;
    DECLARE @LeaseOwnerEpoch BIGINT;
    DECLARE @Watermark BIGINT;
    DECLARE @ExpiresOn DATETIME2(7);
    DECLARE @Status VARCHAR(32);
    DECLARE @Messages TABLE
    (
        MessageId BIGINT,
        StreamIdBytes VARBINARY(MAX),
        StreamNamespaceLength INT,
        CreatedOn DATETIME2(7),
        Payload VARBINARY(MAX)
    );

    BEGIN TRY
        IF @@TRANCOUNT = 0
        BEGIN
            BEGIN TRANSACTION;
            SET @StartedTransaction = 1;
        END;

        SELECT
            @CurrentOwnerEpoch = OwnerEpoch,
            @NextMessageId = NextMessageId,
            @Checkpoint = [Checkpoint]
        FROM OrleansStreamPartition WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        SET @Now = SYSUTCDATETIME();

        SELECT
            @LeaseOwnerEpoch = OwnerEpoch,
            @Watermark = Watermark,
            @ExpiresOn = ExpiresOn
        FROM OrleansStreamReplayLease WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId
            AND ReaderId = @ReaderId;

        SELECT
            @EarliestMessageId = MIN(MessageId),
            @TailMessageId = MAX(MessageId)
        FROM OrleansStreamMessage WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        IF @CurrentOwnerEpoch IS NULL OR @CurrentOwnerEpoch <> @OwnerEpoch
            OR @LeaseOwnerEpoch IS NULL OR @LeaseOwnerEpoch <> @OwnerEpoch
            SET @Status = 'OwnershipLost';
        ELSE IF @ExpiresOn <= @Now
            SET @Status = 'Expired';
        ELSE IF @AfterMessageId < COALESCE(@EarliestMessageId, @NextMessageId) - 1
            SET @Status = 'HistoryUnavailable';
        ELSE
        BEGIN
            SET @ExpiresOn = DATEADD(SECOND, @ReplayLeaseDurationSeconds, @Now);
            UPDATE OrleansStreamReplayLease
            SET ExpiresOn = @ExpiresOn, ModifiedOn = @Now
            WHERE ServiceId = @ServiceId
                AND ProviderId = @ProviderId
                AND QueueId = @QueueId
                AND ReaderId = @ReaderId
                AND OwnerEpoch = @OwnerEpoch;

            INSERT INTO @Messages
            SELECT TOP (@MaxCount) MessageId, StreamIdBytes, StreamNamespaceLength, CreatedOn, Payload
            FROM OrleansStreamMessage WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
            WHERE ServiceId = @ServiceId
                AND ProviderId = @ProviderId
                AND QueueId = @QueueId
                AND MessageId > @AfterMessageId
            ORDER BY MessageId;
            SET @Status = 'Active';
        END;

        IF @StartedTransaction = 1 COMMIT TRANSACTION;

        SELECT
            @Status AS Status,
            @CurrentOwnerEpoch AS OwnerEpoch,
            @Watermark AS Watermark,
            @ExpiresOn AS ExpiresOn,
            @NextMessageId AS NextMessageId,
            @Checkpoint AS [Checkpoint],
            @EarliestMessageId AS EarliestMessageId,
            @TailMessageId AS TailMessageId,
            MessageId,
            StreamIdBytes,
            StreamNamespaceLength,
            CreatedOn,
            Payload
        FROM @Messages
        UNION ALL
        SELECT
            @Status, @CurrentOwnerEpoch, @Watermark, @ExpiresOn, @NextMessageId,
            @Checkpoint, @EarliestMessageId, @TailMessageId,
            NULL, NULL, NULL, NULL, NULL
        WHERE NOT EXISTS (SELECT 1 FROM @Messages)
        ORDER BY MessageId;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE UpdateStreamReplayLease
    @ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @QueueId NVARCHAR(150),
    @ReaderId NVARCHAR(150),
    @OwnerEpoch BIGINT,
    @Watermark BIGINT,
    @ReplayLeaseDurationSeconds INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartedTransaction BIT = 0;
    DECLARE @Now DATETIME2(7);
    DECLARE @CurrentOwnerEpoch BIGINT;
    DECLARE @NextMessageId BIGINT;
    DECLARE @Checkpoint BIGINT;
    DECLARE @EarliestMessageId BIGINT;
    DECLARE @TailMessageId BIGINT;
    DECLARE @LeaseOwnerEpoch BIGINT;
    DECLARE @CurrentWatermark BIGINT;
    DECLARE @ExpiresOn DATETIME2(7);
    DECLARE @Status VARCHAR(32);

    BEGIN TRY
        IF @@TRANCOUNT = 0
        BEGIN
            BEGIN TRANSACTION;
            SET @StartedTransaction = 1;
        END;

        SELECT @CurrentOwnerEpoch = OwnerEpoch, @NextMessageId = NextMessageId, @Checkpoint = [Checkpoint]
        FROM OrleansStreamPartition WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId;
        SET @Now = SYSUTCDATETIME();

        SELECT @LeaseOwnerEpoch = OwnerEpoch, @CurrentWatermark = Watermark, @ExpiresOn = ExpiresOn
        FROM OrleansStreamReplayLease WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId AND ReaderId = @ReaderId;

        SELECT @EarliestMessageId = MIN(MessageId), @TailMessageId = MAX(MessageId)
        FROM OrleansStreamMessage WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId;

        IF @CurrentOwnerEpoch IS NULL OR @CurrentOwnerEpoch <> @OwnerEpoch
            OR @LeaseOwnerEpoch IS NULL OR @LeaseOwnerEpoch <> @OwnerEpoch
            SET @Status = 'OwnershipLost';
        ELSE IF @ExpiresOn <= @Now
            SET @Status = 'Expired';
        ELSE IF @Watermark < COALESCE(@EarliestMessageId, @NextMessageId) - 1
            SET @Status = 'HistoryUnavailable';
        ELSE
        BEGIN
            SET @CurrentWatermark = CASE WHEN @CurrentWatermark < @Watermark THEN @Watermark ELSE @CurrentWatermark END;
            SET @ExpiresOn = DATEADD(SECOND, @ReplayLeaseDurationSeconds, @Now);
            UPDATE OrleansStreamReplayLease
            SET Watermark = @CurrentWatermark, ExpiresOn = @ExpiresOn, ModifiedOn = @Now
            WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId
                AND ReaderId = @ReaderId AND OwnerEpoch = @OwnerEpoch;
            SET @Status = 'Active';
        END;

        IF @StartedTransaction = 1 COMMIT TRANSACTION;

        SELECT @Status AS Status, @CurrentOwnerEpoch AS OwnerEpoch, @CurrentWatermark AS Watermark,
            @ExpiresOn AS ExpiresOn, @NextMessageId AS NextMessageId, @Checkpoint AS [Checkpoint],
            @EarliestMessageId AS EarliestMessageId, @TailMessageId AS TailMessageId;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE ReleaseStreamReplayLease
    @ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @QueueId NVARCHAR(150),
    @ReaderId NVARCHAR(150),
    @OwnerEpoch BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartedTransaction BIT = 0;
    DECLARE @CurrentOwnerEpoch BIGINT;
    DECLARE @NextMessageId BIGINT;
    DECLARE @Checkpoint BIGINT;
    DECLARE @EarliestMessageId BIGINT;
    DECLARE @TailMessageId BIGINT;
    DECLARE @LeaseOwnerEpoch BIGINT;
    DECLARE @Watermark BIGINT;
    DECLARE @ExpiresOn DATETIME2(7);
    DECLARE @Status VARCHAR(32);

    BEGIN TRY
        IF @@TRANCOUNT = 0
        BEGIN
            BEGIN TRANSACTION;
            SET @StartedTransaction = 1;
        END;

        SELECT @CurrentOwnerEpoch = OwnerEpoch, @NextMessageId = NextMessageId, @Checkpoint = [Checkpoint]
        FROM OrleansStreamPartition WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId;

        SELECT @LeaseOwnerEpoch = OwnerEpoch, @Watermark = Watermark, @ExpiresOn = ExpiresOn
        FROM OrleansStreamReplayLease WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId AND ReaderId = @ReaderId;

        SELECT @EarliestMessageId = MIN(MessageId), @TailMessageId = MAX(MessageId)
        FROM OrleansStreamMessage WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId;

        IF @CurrentOwnerEpoch IS NULL OR @CurrentOwnerEpoch <> @OwnerEpoch
            OR (@LeaseOwnerEpoch IS NOT NULL AND @LeaseOwnerEpoch <> @OwnerEpoch)
            SET @Status = 'OwnershipLost';
        ELSE
        BEGIN
            DELETE FROM OrleansStreamReplayLease
            WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId
                AND ReaderId = @ReaderId AND OwnerEpoch = @OwnerEpoch;
            SET @Status = 'Released';
        END;

        IF @StartedTransaction = 1 COMMIT TRANSACTION;

        SELECT @Status AS Status, @CurrentOwnerEpoch AS OwnerEpoch, @Watermark AS Watermark,
            @ExpiresOn AS ExpiresOn, @NextMessageId AS NextMessageId, @Checkpoint AS [Checkpoint],
            @EarliestMessageId AS EarliestMessageId, @TailMessageId AS TailMessageId;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE CleanupStreamMessages
    @ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @QueueId NVARCHAR(150),
    @OwnerEpoch BIGINT,
    @RetentionPeriodSeconds INT,
    @MaximumRetentionPeriodSeconds INT = NULL,
    @CleanupIntervalSeconds INT,
    @CleanupBatchSize INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartedTransaction BIT = 0;
    DECLARE @Now DATETIME2(7);
    DECLARE @CurrentOwnerEpoch BIGINT;
    DECLARE @Checkpoint BIGINT;
    DECLARE @CleanupOn DATETIME2(7);
    DECLARE @ActiveReplayWatermark BIGINT;
    DECLARE @EarliestMessageId BIGINT;
    DECLARE @TailMessageId BIGINT;
    DECLARE @Deleted TABLE (MessageId BIGINT NOT NULL, HardDeleted BIT NOT NULL);

    BEGIN TRY
        IF @@TRANCOUNT = 0
        BEGIN
            BEGIN TRANSACTION;
            SET @StartedTransaction = 1;
        END;

        SELECT
            @CurrentOwnerEpoch = OwnerEpoch,
            @Checkpoint = [Checkpoint],
            @CleanupOn = CleanupOn
        FROM OrleansStreamPartition WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        SET @Now = SYSUTCDATETIME();

        IF @CurrentOwnerEpoch = @OwnerEpoch
        BEGIN
            DELETE FROM OrleansStreamReplayLease
            WHERE ServiceId = @ServiceId
                AND ProviderId = @ProviderId
                AND QueueId = @QueueId
                AND ExpiresOn <= @Now;
        END;

        SELECT @ActiveReplayWatermark = MIN(Watermark)
        FROM OrleansStreamReplayLease WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId
            AND ExpiresOn > @Now;

        SELECT @EarliestMessageId = MIN(MessageId), @TailMessageId = MAX(MessageId)
        FROM OrleansStreamMessage WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId;

        IF @CurrentOwnerEpoch IS NULL OR @CurrentOwnerEpoch <> @OwnerEpoch OR @CleanupOn > @Now
        BEGIN

            SELECT
                CAST(0 AS BIT) AS Ran,
                0 AS DeletedCount,
                CAST(NULL AS BIGINT) AS DeletedThroughMessageId,
                0 AS HardDeletedCount,
                CAST(NULL AS BIGINT) AS HardDeletedFromMessageId,
                CAST(NULL AS BIGINT) AS HardDeletedThroughMessageId,
                @Checkpoint AS [Checkpoint],
                @ActiveReplayWatermark AS ActiveReplayWatermark,
                @EarliestMessageId AS EarliestMessageId,
                @TailMessageId AS TailMessageId;

            IF @StartedTransaction = 1
            BEGIN
                COMMIT TRANSACTION;
            END;

            RETURN;
        END;

        UPDATE OrleansStreamPartition WITH (UPDLOCK, ROWLOCK)
        SET CleanupOn = DATEADD(SECOND, @CleanupIntervalSeconds, @Now), ModifiedOn = @Now
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId
            AND OwnerEpoch = @OwnerEpoch;

        ;WITH Candidate AS
        (
            SELECT TOP (@CleanupBatchSize)
                MessageId
            FROM OrleansStreamMessage WITH (UPDLOCK, READCOMMITTEDLOCK, ROWLOCK)
            WHERE ServiceId = @ServiceId
                AND ProviderId = @ProviderId
                AND QueueId = @QueueId
                AND
                (
                    (
                        @Checkpoint IS NOT NULL
                        AND MessageId <= @Checkpoint
                        AND CheckpointedOn < DATEADD(SECOND, -@RetentionPeriodSeconds, @Now)
                        AND (@ActiveReplayWatermark IS NULL OR MessageId <= @ActiveReplayWatermark)
                    )
                    OR
                    (
                        @MaximumRetentionPeriodSeconds IS NOT NULL
                        AND CreatedOn < DATEADD(SECOND, -@MaximumRetentionPeriodSeconds, @Now)
                    )
                )
            ORDER BY MessageId
        )
        DELETE Message
        OUTPUT
            Deleted.MessageId,
            CASE
                WHEN
                    @MaximumRetentionPeriodSeconds IS NOT NULL
                    AND Deleted.CreatedOn < DATEADD(SECOND, -@MaximumRetentionPeriodSeconds, @Now)
                    AND NOT
                    (
                        @Checkpoint IS NOT NULL
                        AND Deleted.MessageId <= @Checkpoint
                        AND Deleted.CheckpointedOn < DATEADD(SECOND, -@RetentionPeriodSeconds, @Now)
                        AND (@ActiveReplayWatermark IS NULL OR Deleted.MessageId <= @ActiveReplayWatermark)
                    )
                    THEN CAST(1 AS BIT)
                ELSE CAST(0 AS BIT)
            END
        INTO @Deleted (MessageId, HardDeleted)
        FROM OrleansStreamMessage AS Message
        INNER JOIN Candidate
            ON Candidate.MessageId = Message.MessageId
        WHERE Message.ServiceId = @ServiceId
            AND Message.ProviderId = @ProviderId
            AND Message.QueueId = @QueueId;

        SELECT
            CAST(1 AS BIT) AS Ran,
            COUNT(*) AS DeletedCount,
            MAX(MessageId) AS DeletedThroughMessageId,
            COALESCE(SUM(CASE WHEN HardDeleted = 1 THEN 1 ELSE 0 END), 0) AS HardDeletedCount,
            MIN(CASE WHEN HardDeleted = 1 THEN MessageId END) AS HardDeletedFromMessageId,
            MAX(CASE WHEN HardDeleted = 1 THEN MessageId END) AS HardDeletedThroughMessageId,
            @Checkpoint AS [Checkpoint],
            @ActiveReplayWatermark AS ActiveReplayWatermark,
            (
                SELECT MIN(MessageId)
                FROM OrleansStreamMessage
                WHERE ServiceId = @ServiceId
                    AND ProviderId = @ProviderId
                    AND QueueId = @QueueId
            ) AS EarliestMessageId,
            (
                SELECT MAX(MessageId)
                FROM OrleansStreamMessage
                WHERE ServiceId = @ServiceId
                    AND ProviderId = @ProviderId
                    AND QueueId = @QueueId
            ) AS TailMessageId
        FROM @Deleted;

        IF @StartedTransaction = 1
        BEGIN
            COMMIT TRANSACTION;
        END;
    END TRY
    BEGIN CATCH
        IF @StartedTransaction = 1 AND XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;
END;
GO

INSERT INTO OrleansQuery (QueryKey, QueryText)
VALUES
    ('StreamSchemaVersionKey', '3'),
    ('AppendStreamMessageKey', 'EXECUTE AppendStreamMessage @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @StreamIdBytes = @StreamIdBytes, @StreamNamespaceLength = @StreamNamespaceLength, @Payload = @Payload'),
    ('AcquireStreamPartitionKey', 'EXECUTE AcquireStreamPartition @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @StartFromNow = @StartFromNow'),
    ('ReadStreamMessagesKey', 'SELECT ServiceId, ProviderId, QueueId, MessageId, StreamIdBytes, StreamNamespaceLength, CreatedOn, Payload FROM OrleansStreamMessage WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId AND MessageId > @AfterMessageId ORDER BY MessageId OFFSET 0 ROWS FETCH NEXT @MaxCount ROWS ONLY'),
    ('AdvanceStreamCheckpointKey', 'EXECUTE AdvanceStreamCheckpoint @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @OwnerEpoch = @OwnerEpoch, @Checkpoint = @Checkpoint'),
    ('GetStreamPartitionBoundsKey', 'SELECT P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.NextMessageId, P.[Checkpoint] AS [Checkpoint], MIN(M.MessageId) AS EarliestMessageId, MAX(M.MessageId) AS TailMessageId FROM OrleansStreamPartition AS P LEFT JOIN OrleansStreamMessage AS M ON M.ServiceId = P.ServiceId AND M.ProviderId = P.ProviderId AND M.QueueId = P.QueueId WHERE P.ServiceId = @ServiceId AND P.ProviderId = @ProviderId AND P.QueueId = @QueueId GROUP BY P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.NextMessageId, P.[Checkpoint]'),
    ('AcquireStreamReplayLeaseKey', 'EXECUTE AcquireStreamReplayLease @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @ReaderId = @ReaderId, @StreamIdBytes = @StreamIdBytes, @StreamNamespaceLength = @StreamNamespaceLength, @OwnerEpoch = @OwnerEpoch, @AfterMessageId = @AfterMessageId, @ReplayLeaseDurationSeconds = @ReplayLeaseDurationSeconds'),
    ('ReadStreamReplayMessagesKey', 'EXECUTE ReadStreamReplayMessages @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @ReaderId = @ReaderId, @OwnerEpoch = @OwnerEpoch, @AfterMessageId = @AfterMessageId, @MaxCount = @MaxCount, @ReplayLeaseDurationSeconds = @ReplayLeaseDurationSeconds'),
    ('UpdateStreamReplayLeaseKey', 'EXECUTE UpdateStreamReplayLease @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @ReaderId = @ReaderId, @OwnerEpoch = @OwnerEpoch, @Watermark = @Watermark, @ReplayLeaseDurationSeconds = @ReplayLeaseDurationSeconds'),
    ('ReleaseStreamReplayLeaseKey', 'EXECUTE ReleaseStreamReplayLease @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @ReaderId = @ReaderId, @OwnerEpoch = @OwnerEpoch'),
    ('CleanupStreamMessagesKey', 'EXECUTE CleanupStreamMessages @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @OwnerEpoch = @OwnerEpoch, @RetentionPeriodSeconds = @RetentionPeriodSeconds, @MaximumRetentionPeriodSeconds = @MaximumRetentionPeriodSeconds, @CleanupIntervalSeconds = @CleanupIntervalSeconds, @CleanupBatchSize = @CleanupBatchSize');
GO
