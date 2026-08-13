/*
ADO.NET streaming schema version 2.

This alpha schema is intentionally incompatible with the former destructive queue schema.
Drop the former streaming tables, sequence, routines, and OrleansQuery rows before applying
this script. Existing queue rows are not migrated.
*/

IF OBJECT_ID(N'OrleansStreamPartition', N'U') IS NOT NULL
    OR OBJECT_ID(N'OrleansStreamMessage', N'U') IS NOT NULL
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
    THROW 51001, 'Incompatible alpha ADO.NET streaming schema. Drop old streaming tables, sequence, routines, and OrleansQuery rows before applying version 2; no in-place migration is supported.', 1;
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
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
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
                    @Now,
                    @Now,
                    @Now
                );
            END;
        END;

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
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
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
                    @Now,
                    @Now,
                    @Now
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

    DECLARE @Result TABLE
    (
        ServiceId NVARCHAR(150) NOT NULL,
        ProviderId NVARCHAR(150) NOT NULL,
        QueueId NVARCHAR(150) NOT NULL,
        OwnerEpoch BIGINT NOT NULL,
        [Checkpoint] BIGINT NULL,
        Updated BIT NOT NULL
    );

    UPDATE OrleansStreamPartition WITH (UPDLOCK, ROWLOCK)
    SET
        [Checkpoint] = @Checkpoint,
        ModifiedOn = SYSUTCDATETIME()
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
END;
GO

CREATE PROCEDURE CleanupStreamMessages
    @ServiceId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @QueueId NVARCHAR(150),
    @RetentionPeriodSeconds INT,
    @MaximumRetentionPeriodSeconds INT = NULL,
    @CleanupIntervalSeconds INT,
    @CleanupBatchSize INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @StartedTransaction BIT = 0;
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Checkpoint BIGINT;
    DECLARE @Deleted TABLE (MessageId BIGINT NOT NULL, HardDeleted BIT NOT NULL);

    BEGIN TRY
        IF @@TRANCOUNT = 0
        BEGIN
            BEGIN TRANSACTION;
            SET @StartedTransaction = 1;
        END;

        UPDATE OrleansStreamPartition WITH (UPDLOCK, ROWLOCK)
        SET
            CleanupOn = DATEADD(SECOND, @CleanupIntervalSeconds, @Now),
            ModifiedOn = @Now,
            @Checkpoint = [Checkpoint]
        WHERE ServiceId = @ServiceId
            AND ProviderId = @ProviderId
            AND QueueId = @QueueId
            AND CleanupOn <= @Now;

        IF @@ROWCOUNT = 0
        BEGIN
            SELECT @Checkpoint = [Checkpoint]
            FROM OrleansStreamPartition
            WHERE ServiceId = @ServiceId
                AND ProviderId = @ProviderId
                AND QueueId = @QueueId;

            SELECT
                CAST(0 AS BIT) AS Ran,
                0 AS DeletedCount,
                CAST(NULL AS BIGINT) AS DeletedThroughMessageId,
                0 AS HardDeletedCount,
                CAST(NULL AS BIGINT) AS HardDeletedFromMessageId,
                CAST(NULL AS BIGINT) AS HardDeletedThroughMessageId,
                @Checkpoint AS [Checkpoint],
                MIN(MessageId) AS EarliestMessageId,
                MAX(MessageId) AS TailMessageId
            FROM OrleansStreamMessage
            WHERE ServiceId = @ServiceId
                AND ProviderId = @ProviderId
                AND QueueId = @QueueId;

            IF @StartedTransaction = 1
            BEGIN
                COMMIT TRANSACTION;
            END;

            RETURN;
        END;

        ;WITH Candidate AS
        (
            SELECT TOP (@CleanupBatchSize)
                MessageId
            FROM OrleansStreamMessage WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE ServiceId = @ServiceId
                AND ProviderId = @ProviderId
                AND QueueId = @QueueId
                AND
                (
                    (
                        @Checkpoint IS NOT NULL
                        AND MessageId <= @Checkpoint
                        AND CreatedOn < DATEADD(SECOND, -@RetentionPeriodSeconds, @Now)
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
                WHEN @Checkpoint IS NULL OR Deleted.MessageId > @Checkpoint THEN CAST(1 AS BIT)
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
    ('StreamSchemaVersionKey', '2'),
    ('AppendStreamMessageKey', 'EXECUTE AppendStreamMessage @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @StreamIdBytes = @StreamIdBytes, @StreamNamespaceLength = @StreamNamespaceLength, @Payload = @Payload'),
    ('AcquireStreamPartitionKey', 'EXECUTE AcquireStreamPartition @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @StartFromNow = @StartFromNow'),
    ('ReadStreamMessagesKey', 'SELECT ServiceId, ProviderId, QueueId, MessageId, StreamIdBytes, StreamNamespaceLength, CreatedOn, Payload FROM OrleansStreamMessage WHERE ServiceId = @ServiceId AND ProviderId = @ProviderId AND QueueId = @QueueId AND MessageId > @AfterMessageId ORDER BY MessageId OFFSET 0 ROWS FETCH NEXT @MaxCount ROWS ONLY'),
    ('AdvanceStreamCheckpointKey', 'EXECUTE AdvanceStreamCheckpoint @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @OwnerEpoch = @OwnerEpoch, @Checkpoint = @Checkpoint'),
    ('GetStreamPartitionBoundsKey', 'SELECT P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.[Checkpoint] AS [Checkpoint], MIN(M.MessageId) AS EarliestMessageId, MAX(M.MessageId) AS TailMessageId FROM OrleansStreamPartition AS P LEFT JOIN OrleansStreamMessage AS M ON M.ServiceId = P.ServiceId AND M.ProviderId = P.ProviderId AND M.QueueId = P.QueueId WHERE P.ServiceId = @ServiceId AND P.ProviderId = @ProviderId AND P.QueueId = @QueueId GROUP BY P.ServiceId, P.ProviderId, P.QueueId, P.OwnerEpoch, P.[Checkpoint]'),
    ('CleanupStreamMessagesKey', 'EXECUTE CleanupStreamMessages @ServiceId = @ServiceId, @ProviderId = @ProviderId, @QueueId = @QueueId, @RetentionPeriodSeconds = @RetentionPeriodSeconds, @MaximumRetentionPeriodSeconds = @MaximumRetentionPeriodSeconds, @CleanupIntervalSeconds = @CleanupIntervalSeconds, @CleanupBatchSize = @CleanupBatchSize');
GO
