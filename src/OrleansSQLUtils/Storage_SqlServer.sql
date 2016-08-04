
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
-- memory and should it require maintenance, it should be non-resource intensive. For this
-- reason the index is NONCLUSTERED by design. Currently the entity is recognized in the storage by
-- the grain type and its ID, which are unique in Orleans silo. As adding two
-- NVARCHAR(150) fields into a index would make it more resource intensive, the values
-- are hashed into two INT type instance, which are made a compound index. When there are no
-- collisions, the index can quickly locate the unique row. Along with the hashed index
-- values, the NVARCHAR(150) values are also stored and they are used to prune hash
-- collisions down to only one result row.
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
DROP TABLE Storage;
CREATE TABLE Storage
(
    -- These are for the book keeping. Orleans calculates
    -- these hashes (Jenkins), which are unsigned 32 integers mapped to
    -- the *Hash fields. The mapping is done in the code. The
    -- *String columns contain the corresponding clear name fields.
	--
	-- If there are duplicates, they are resolved by using GrainIdString
	-- and GrainNameString fields. It is assumed these would be rarely needed.
    GrainIdHash     INT NOT NULL,
    GrainIdString   NVARCHAR(150) NOT NULL,
    GrainTypeHash   INT NOT NULL,
    GrainTypeString NVARCHAR(150) NOT NULL,
	ServiceId NVARCHAR(150) NOT NULL,

    -- The usage of the Payload records is exclusive in that
    -- only one is populated at any given time and two others
    -- are NULL. When all three are returned, the application
    -- knows how to handle the situation. The advantange on separating
	-- these by types is that various DB engines include additional
	-- in-storage processing and "compression" capabilities depending on type.
	--
	-- One is free to alter the size of these fields, MAX isn't mandated.
    PayloadBinary VARBINARY(MAX) NULL,
    PayloadXml XML NULL,
    PayloadJson NVARCHAR(MAX) NULL,

    -- Informational field, no other use.
    ModifiedOn DATETIME2(3) NOT NULL,

    -- If this particular object has been deleted from the database
    -- or not. The objects can be inserted, deleted and reinserted.    
    Version INT NULL

    -- The following would in principle be the primary key, but it would be too thick
	-- to be indexed, so the the values are hashed and only collisions will be solved
	-- by using the fields. That is, after the indexed queries have pinpointed the right
	-- rows down to [0, n] relevant ones, n being the number of collided value pairs.
    -- CONSTRAINT PK_Storage PRIMARY KEY NONCLUSTERED (GrainIdString, GrainTypeString)        
);
CREATE NONCLUSTERED INDEX IX_Storage ON Storage(GrainIdHash, GrainTypeHash);

-- A feature with ID is compression. If it is supported, it is used for Storage table. This is an Enterprise feature.
-- This consumes more processor cycles, but should save on space on GrainIdString, GrainTypeString and ServiceId, which
-- contain mainly the same values. 
IF EXISTS (SELECT 1 FROM sys.dm_db_persisted_sku_features WHERE feature_id = 100)
BEGIN
	ALTER TABLE Storage REBUILD PARTITION = ALL WITH(DATA_COMPRESSION = PAGE);
END


-- This INSERT INTO isn't valid syntax for Oracle.
INSERT INTO Storage
(
	GrainIdHash,
	GrainIdString,
	GrainTypeHash,
	GrainTypeString,
	ServiceId,
	PayloadBinary,
	PayloadJson,
	PayloadXml,
	ModifiedOn,
	Version
)
SELECT
	A.x,
	(N'GrainIdString_' + CAST(A.x AS NVARCHAR)),
	B.x,
	(N'GrainTypeString_' + CAST(B.x AS NVARCHAR)),
	N'TestService',
	NULL,
	N'{ "SomeObjectPayload": "' + (N'GrainIdString_' + CAST(A.x AS NVARCHAR)) + N'__' + (N'GrainTypeString_' + CAST(B.x AS NVARCHAR)) + N'" }',
	NULL,
	GETUTCDATE(),
	1
FROM
(	
	SELECT(kilo * 1000 + hecto * 100 + deca * 10 + unit + 1) AS x
	FROM
	--(
		-- SELECT 0 AS myria
		-- UNION ALL SELECT 1
		-- UNION ALL SELECT 2
		-- UNION ALL SELECT 3		
	--) AS my,
	(
		SELECT 0 AS kilo
		UNION ALL SELECT 1
		UNION ALL SELECT 2
		UNION ALL SELECT 3
		UNION ALL SELECT 4
		UNION ALL SELECT 5
		UNION ALL SELECT 6
		UNION ALL SELECT 7
		UNION ALL SELECT 8
		UNION ALL SELECT 9
	) AS k,
	(
		SELECT 0 AS hecto
		UNION ALL SELECT 1
		UNION ALL SELECT 2
		UNION ALL SELECT 3
		UNION ALL SELECT 4
		UNION ALL SELECT 5
		UNION ALL SELECT 6
		UNION ALL SELECT 7
		UNION ALL SELECT 8
		UNION ALL SELECT 9
	) AS ha,
	(
		SELECT 0 AS deca
		UNION ALL SELECT 1
		UNION ALL SELECT 2
		UNION ALL SELECT 3
		UNION ALL SELECT 4
		UNION ALL SELECT 5
		UNION ALL SELECT 6
		UNION ALL SELECT 7
		UNION ALL SELECT 8
		UNION ALL SELECT 9
	) AS da,
	(
		SELECT 0 AS unit
		UNION ALL SELECT 1
		UNION ALL SELECT 2
		UNION ALL SELECT 3
		UNION ALL SELECT 4
		UNION ALL SELECT 5
		UNION ALL SELECT 6
		UNION ALL SELECT 7
		UNION ALL SELECT 8
		UNION ALL SELECT 9
	) AS u
) AS A CROSS JOIN
(
	SELECT(hecto * 100 + deca * 10 + unit + 1) AS x
	FROM
	(
		SELECT 0 AS hecto
		UNION ALL SELECT 1
		UNION ALL SELECT 2
		UNION ALL SELECT 3
		UNION ALL SELECT 4
		UNION ALL SELECT 5
		UNION ALL SELECT 6
		UNION ALL SELECT 7
		UNION ALL SELECT 8
		UNION ALL SELECT 9
	) AS ha,
	(
		SELECT 0 AS deca
		UNION ALL SELECT 1
		UNION ALL SELECT 2
		UNION ALL SELECT 3
		UNION ALL SELECT 4
		UNION ALL SELECT 5
		UNION ALL SELECT 6
		UNION ALL SELECT 7
		UNION ALL SELECT 8
		UNION ALL SELECT 9
	) AS da,
	(
		SELECT 0 AS unit
		UNION ALL SELECT 1
		UNION ALL SELECT 2
		UNION ALL SELECT 3
		UNION ALL SELECT 4
		UNION ALL SELECT 5
		UNION ALL SELECT 6
		UNION ALL SELECT 7
		UNION ALL SELECT 8
		UNION ALL SELECT 9
	) AS u	
) AS B;


DECLARE @GrainIdHash AS INT = 1;
DECLARE @GrainIdString AS NVARCHAR(150) = N'GrainIdString_1';
DECLARE @GrainTypeHash  AS INT = 1;
DECLARE @GrainTypeString AS NVARCHAR(150) = N'GrainTypeString_1';
DECLARE @ServiceId AS NVARCHAR(150) = N'TestService';

DECLARE @PayloadBinary AS VARBINARY(MAX);
DECLARE @PayloadJson AS NVARCHAR(MAX);
DECLARE @PayloadXml AS XML;

DECLARE @GrainStateVersion AS INT = 0;

-- When Orleans is running in normal, non-split state, there will
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
-- See further information at See further at http://dotnet.github.io/orleans/Getting-Started-With-Orleans/Grain-Persistence.
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
		@NewGrainStateVersion = Version + 1
	WHERE
		GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
		AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
		AND GrainIdString = @GrainIdString AND @GrainIdString IS NOT NULL
		AND GrainTypeString = @GrainTypeString AND @GrainTypeString IS NOT NULL
		AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND Version IS NOT NULL AND Version = @GrainStateVersion AND @GrainStateVersion IS NOT NULL
		OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));
END

-- If no rows were updated, the reason could be that a new storage may have
-- been brought online or it has been reset somehow while the silo is running
-- having the grains in memory. Let's try to insert them anyhow as it is more
-- more robust.
IF @@ROWCOUNT = 0
BEGIN
	INSERT INTO Storage
	(
		GrainIdHash,
		GrainIdString,
		GrainTypeHash,
		GrainTypeString,
		ServiceId,
		PayloadBinary,
		PayloadJson,
		PayloadXml,
		ModifiedOn,
		Version
	)
	SELECT
		@GrainIdHash,
		@GrainIdString,
		@GrainTypeHash,
		@GrainTypeString,
		@ServiceId,
		@PayloadBinary,
		@PayloadJson,
		@PayloadXml,
		GETUTCDATE(),
		1
	 WHERE NOT EXISTS
	 (
		SELECT 1
		FROM Storage WITH(UPDLOCK, HOLDLOCK)
		WHERE
			GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
			AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
			AND GrainIdString = @GrainIdString AND @GrainIdString IS NOT NULL
			AND GrainTypeString = @GrainTypeString AND @GrainTypeString IS NOT NULL
			AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
			AND Version IS NOT NULL
	 ) OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));

	SET @NewGrainStateVersion = @@ROWCOUNT;
END

SELECT @NewGrainStateVersion;
COMMIT TRANSACTION;


INSERT INTO Storage
(
	GrainIdHash,
	GrainIdString,
	GrainTypeHash,
	GrainTypeString,
	PayloadBinary,
	PayloadJson,
	PayloadXml,
	ModifiedOn,
	Version
)
VALUES
(	
	@GrainIdHash,
	N'GrainIdString_1_duplicate',
	@GrainTypeHash,
	N'GrainTypeString_1_duplicate',
	@ServiceId,
	NULL,
	N'{ "SomeObjectPayload": "GrainIdString_1__GrainTypeString_1_duplicate" }"',
	NULL,
	GETUTCDATE(),
	1	
);

-- The application code will deserialize the relevant result. Not that the query optimizer
-- estimates the result of rows based on its knowledge on the index. It doesn't know there
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
	GrainIdHash = @GrainIdHash
	AND GrainTypeHash = @GrainTypeHash
	AND GrainIdString = @GrainIdString
	AND GrainTypeString = @GrainTypeString
	OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));
	
-- The application code will deserialize the relevant result. Not that the query optimizer
-- estimates the result of rows based on its knowledge on the index. It doesn't know there
-- will be only one row returned. Forcing the optimizer to process the first found row quickly
-- creates an estimate for a one-row result and makes a difference on multi-million row tables.
-- Also the optimizer is instructed to always use the same plan via index using the OPTIMIZE
-- FOR UNKNOWN flags. These hints are only available in SQL Server 2008 and later. They
-- should guarantee the execution time is robustly basically the same from query-to-query.
BEGIN TRANSACTION;
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
	AND GrainIdString = @GrainIdString AND @GrainIdString IS NOT NULL
	AND GrainTypeString = @GrainTypeString AND @GrainTypeString IS NOT NULL
	AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
	AND Version IS NOT NULL AND Version = @GrainStateVersion AND @GrainStateVersion IS NOT NULL
	OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));

SELECT @NewGrainStateVersion;
COMMIT TRANSACTION;


-- With the INCLUDE columns the effect of the previous definition
-- should be more like this in "procedural SQL".
SELECT
    PayloadBinary,
    PayloadXml,
    PayloadJson,
	Version
FROM
(
    SELECT
        PayloadBinary,
		PayloadXml,
		PayloadJson,
        GrainIdString,
        GrainTypeString
    FROM
        Storage
    WHERE
        GrainIdHash = @GrainIdHash
        AND GrainTypeHash = @GrainTypeHash        
) AS collidedRows
WHERE
	collidedRows.GrainIdString = @GrainIdString
	AND collidedRows.GrainTypeString = @GrainTypeString OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));