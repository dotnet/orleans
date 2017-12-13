-- The design criteria for this table are:
--
-- 1. It can contain arbitrary content serialized as binary, XML or JSON. These formats
-- are supported to allow one to take advantage of in-storage processing capabilities for
-- these types if required. This should not incur extra cost on storage.
--
-- 2. The table design should scale with the idea of tens or hundreds (or even more) types
-- of grains that may operate with even hundreds of thousands of grain IDs within each
-- type of a grain.
--
-- 3. The table and its associated operations should remain stable. There should not be
-- structural reason for unexpected delays in operations. It should be possible to also
-- insert data reasonably fast without resource contention.
--
-- 4. For reasons in 2. and 3., the index should be as narrow as possible so it fits well in
-- memory and should it require maintenance, isn't resource intensive. For this
-- reason the index is narrow by design (ideally non-clustered). Currently the entity
-- is recognized in the storage by the grain type and its ID, which are unique in Orleans silo.
-- The ID is the grain ID bytes (if string type UTF-8 bytes) and possible extension key as UTF-8
-- bytes concatenated with the ID and then hashed.
--
-- Reason for hashing: Database engines usually limit the length of the column sizes, which
-- would artificially limit the length of IDs or types. Even when within limitations, the
-- index would be thick and consume more memory.
--
-- In the current setup the ID and the type are hashed into two INT type instances, which
-- are made a compound index. When there are no collisions, the index can quickly locate
-- the unique row. Along with the hashed index values, the NVARCHAR(nnn) values are also
-- stored and they are used to prune hash collisions down to only one result row.
--
-- 5. The design leads to duplication in the storage. It is reasonable to assume there will
-- a low number of services with a given service ID operational at any given time. Or that
-- compared to the number of grain IDs, there are a fairly low number of different types of
-- grain. The catch is that were these data separated to another table, it would make INSERT
-- and UPDATE operations complicated and would require joins, temporary variables and additional
-- indexes or some combinations of them to make it work. It looks like fitting strategy
-- could be to use table compression.
--
-- 6. For the aforementioned reasons, grain state DELETE will set NULL to the data fields
-- and updates the Version number normally. This should alleviate the need for index or
-- statistics maintenance with the loss of some bytes of storage space. The table can be scrubbed
-- in a separate maintenance operation.
--
-- 7. In the storage operations queries the columns need to be in the exact same order
-- since the storage table operations support optionally streaming.
CREATE TABLE Storage
(
    -- These are for the book keeping. Orleans calculates
    -- these hashes (see RelationalStorageProvide implementation),
    -- which are signed 32 bit integers mapped to the *Hash fields.
    -- The mapping is done in the code. The
    -- *String columns contain the corresponding clear name fields.
    --
    -- If there are duplicates, they are resolved by using GrainIdN0,
    -- GrainIdN1, GrainIdExtensionString and GrainTypeString fields.
    -- It is assumed these would be rarely needed.
    GrainIdHash                INT NOT NULL,
    GrainIdN0                BIGINT NOT NULL,
    GrainIdN1                BIGINT NOT NULL,
    GrainTypeHash            INT NOT NULL,
    GrainTypeString            NVARCHAR(512) NOT NULL,
    GrainIdExtensionString    NVARCHAR(512) NULL,
    ServiceId                NVARCHAR(150) NOT NULL,

    -- The usage of the Payload records is exclusive in that
    -- only one should be populated at any given time and two others
    -- are NULL. The types are separated to advantage on special
    -- processing capabilities present on database engines (not all might
    -- have both JSON and XML types.
    --
    -- One is free to alter the size of these fields.
    PayloadBinary    VARBINARY(MAX) NULL,
    PayloadXml        XML NULL,
    PayloadJson        NVARCHAR(MAX) NULL,

    -- Informational field, no other use.
    ModifiedOn DATETIME2(3) NOT NULL,

    -- The version of the stored payload.
    Version INT NULL

    -- The following would in principle be the primary key, but it would be too thick
    -- to be indexed, so the values are hashed and only collisions will be solved
    -- by using the fields. That is, after the indexed queries have pinpointed the right
    -- rows down to [0, n] relevant ones, n being the number of collided value pairs.
);

CREATE NONCLUSTERED INDEX IX_Storage ON Storage(GrainIdHash, GrainTypeHash);

-- This ensures lock escalation will not lock the whole table, which can potentially be enormous.
-- See more information at https://www.littlekendra.com/2016/02/04/why-rowlock-hints-can-make-queries-slower-and-blocking-worse-in-sql-server/.
ALTER TABLE Storage SET(LOCK_ESCALATION = DISABLE);

-- A feature with ID is compression. If it is supported, it is used for Storage table. This is an Enterprise feature.
-- This consumes more processor cycles, but should save on space on GrainIdString, GrainTypeString and ServiceId, which
-- contain mainly the same values. Also the payloads will be compressed.
IF EXISTS (SELECT 1 FROM sys.dm_db_persisted_sku_features WHERE feature_id = 100)
BEGIN
    ALTER TABLE Storage REBUILD PARTITION = ALL WITH(DATA_COMPRESSION = PAGE);
END

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'WriteToStorageKey',
    '-- When Orleans is running in normal, non-split state, there will
    -- be only one grain with the given ID and type combination only. This
    -- grain saves states mostly serially if Orleans guarantees are upheld. Even
    -- if not, the updates should work correctly due to version number.
    --
    -- In split brain situations there can be a situation where there are two or more
    -- grains with the given ID and type combination. When they try to INSERT
    -- concurrently, the table needs to be locked pessimistically before one of
    -- the grains gets @GrainStateVersion = 1 in return and the other grains will fail
    -- to update storage. The following arrangement is made to reduce locking in normal operation.
    --
    -- If the version number explicitly returned is still the same, Orleans interprets it so the update did not succeed
    -- and throws an InconsistentStateException.
    --
    -- See further information at http://dotnet.github.io/orleans/Getting-Started-With-Orleans/Grain-Persistence.
    BEGIN TRANSACTION;
    SET XACT_ABORT, NOCOUNT ON;

    DECLARE @NewGrainStateVersion AS INT = @GrainStateVersion;


    -- If the @GrainStateVersion is not zero, this branch assumes it exists in this database.
    -- The NULL value is supplied by Orleans when the state is new.
    IF @GrainStateVersion IS NOT NULL
    BEGIN
        UPDATE Storage
        SET
            PayloadBinary = @PayloadBinary,
            PayloadJson = @PayloadJson,
            PayloadXml = @PayloadXml,
            ModifiedOn = GETUTCDATE(),
            Version = Version + 1,
            @NewGrainStateVersion = Version + 1,
            @GrainStateVersion = Version + 1
        WHERE
            GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
            AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
            AND (GrainIdN0 = @GrainIdN0 OR @GrainIdN0 IS NULL)
            AND (GrainIdN1 = @GrainIdN1 OR @GrainIdN1 IS NULL)
            AND (GrainTypeString = @GrainTypeString OR @GrainTypeString IS NULL)
            AND ((@GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = @GrainIdExtensionString) OR @GrainIdExtensionString IS NULL AND GrainIdExtensionString IS NULL)
            AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
            AND Version IS NOT NULL AND Version = @GrainStateVersion AND @GrainStateVersion IS NOT NULL
            OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));
    END

    -- The grain state has not been read. The following locks rather pessimistically
    -- to ensure only one INSERT succeeds.
    IF @GrainStateVersion IS NULL
    BEGIN
        INSERT INTO Storage
        (
            GrainIdHash,
            GrainIdN0,
            GrainIdN1,
            GrainTypeHash,
            GrainTypeString,
            GrainIdExtensionString,
            ServiceId,
            PayloadBinary,
            PayloadJson,
            PayloadXml,
            ModifiedOn,
            Version
        )
        SELECT
            @GrainIdHash,
            @GrainIdN0,
            @GrainIdN1,
            @GrainTypeHash,
            @GrainTypeString,
            @GrainIdExtensionString,
            @ServiceId,
            @PayloadBinary,
            @PayloadJson,
            @PayloadXml,
            GETUTCDATE(),
            1
         WHERE NOT EXISTS
         (
            -- There should not be any version of this grain state.
            SELECT 1
            FROM Storage WITH(XLOCK, ROWLOCK, HOLDLOCK, INDEX(IX_Storage))
            WHERE
                GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
                AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
                AND (GrainIdN0 = @GrainIdN0 OR @GrainIdN0 IS NULL)
                AND (GrainIdN1 = @GrainIdN1 OR @GrainIdN1 IS NULL)
                AND (GrainTypeString = @GrainTypeString OR @GrainTypeString IS NULL)
                AND ((@GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = @GrainIdExtensionString) OR @GrainIdExtensionString IS NULL AND GrainIdExtensionString IS NULL)
                AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
         ) OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));

        IF @@ROWCOUNT > 0
        BEGIN
            SET @NewGrainStateVersion = 1;
        END
    END

    SELECT @NewGrainStateVersion AS NewGrainStateVersion;
    COMMIT TRANSACTION;'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ClearStorageKey',
    'BEGIN TRANSACTION;
    SET XACT_ABORT, NOCOUNT ON;
    DECLARE @NewGrainStateVersion AS INT = @GrainStateVersion;
    UPDATE Storage
    SET
        PayloadBinary = NULL,
        PayloadJson = NULL,
        PayloadXml = NULL,
        ModifiedOn = GETUTCDATE(),
        Version = Version + 1,
        @NewGrainStateVersion = Version + 1
    WHERE
        GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
        AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
        AND (GrainIdN0 = @GrainIdN0 OR @GrainIdN0 IS NULL)
        AND (GrainIdN1 = @GrainIdN1 OR @GrainIdN1 IS NULL)
        AND (GrainTypeString = @GrainTypeString OR @GrainTypeString IS NULL)
        AND ((@GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = @GrainIdExtensionString) OR @GrainIdExtensionString IS NULL AND GrainIdExtensionString IS NULL)
        AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        AND Version IS NOT NULL AND Version = @GrainStateVersion AND @GrainStateVersion IS NOT NULL
        OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));

    SELECT @NewGrainStateVersion;
    COMMIT TRANSACTION;'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'ReadFromStorageKey',
    '-- The application code will deserialize the relevant result. Not that the query optimizer
    -- estimates the result of rows based on its knowledge on the index. It does not know there
    -- will be only one row returned. Forcing the optimizer to process the first found row quickly
    -- creates an estimate for a one-row result and makes a difference on multi-million row tables.
    -- Also the optimizer is instructed to always use the same plan via index using the OPTIMIZE
    -- FOR UNKNOWN flags. These hints are only available in SQL Server 2008 and later. They
    -- should guarantee the execution time is robustly basically the same from query-to-query.
    SELECT
        PayloadBinary,
        PayloadXml,
        PayloadJson,
        Version
    FROM
        Storage
    WHERE
        GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
        AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
        AND (GrainIdN0 = @GrainIdN0 OR @GrainIdN0 IS NULL)
        AND (GrainIdN1 = @GrainIdN1 OR @GrainIdN1 IS NULL)
        AND (GrainTypeString = @GrainTypeString OR @GrainTypeString IS NULL)
        AND ((@GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString IS NOT NULL AND GrainIdExtensionString = @GrainIdExtensionString) OR @GrainIdExtensionString IS NULL AND GrainIdExtensionString IS NULL)
        AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
        OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));'
);
