/*
Orleans Grain Directory.

This tables stores the location of all grains in the cluster.

The rationale for this table is as follows:

1. The table will see rows inserted individually, as new grains are added.
2. The table will see rows deleted at random as grains are deactivated, without regard for order.
3. Insert/Delete churn is expected to be very high.
4. The GrainId is too large to be indexed by SQL Server.

Given the above, the table cannot be a clustered index.
Not only is the GrainId too large to index directly, the expected insert/delete churn would cause fragmentation to the point of rendering the directory unusable.
Therefore the design choice is to use a heap table with a non-clustered index on the stable hash of the grain key.
Uniqueness is then guaranteed by careful use of locks on the hash index.
*/
CREATE TABLE OrleansGrainDirectory
(
    /* Identifies the cluster instance */
    ClusterId NVARCHAR(150) NOT NULL,

    /* Identifies the grain directory provider */
    ProviderId NVARCHAR(150) NOT NULL,

    /* Holds the hash of the grain id */
    GrainIdHash INT NOT NULL,

    /* Holds the grain id in text form */
    GrainId TEXT NOT NULL,

    /* Holds the silo address where the grain is located */
    SiloAddress NVARCHAR(100) NOT NULL,

    /* Holds the activation id in the silo where it is located */
    ActivationId NVARCHAR(100) NOT NULL,

    /* Holds the time at which the grain was added to the directory */
    CreatedOn DATETIME(3) NOT NULL
);

DELIMITER $$

/*
This index is a workaround for the GrainId being too large to index by SQL Server.
Instead we index a stable hash of the GrainId.
Collisions are possible yet handled by careful use of locks on this index.
*/
CREATE INDEX IX_OrleansGrainDirectory_Lookup
ON OrleansGrainDirectory
(
    ClusterId ASC,
    ProviderId ASC,
    GrainIdHash ASC
);

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'RegisterGrainActivationKey',
	'
    SET AUTOCOMMIT = 0;
    LOCK TABLES OrleansGrainDirectory WRITE;

    SELECT
        ClusterId,
        ProviderId,
        GrainId,
        SiloAddress,
        ActivationId
    FROM
        OrleansGrainDirectory
    WHERE
        ClusterId = @ClusterId
        AND ProviderId = @ProviderId
        AND GrainIdHash = @GrainIdHash
        AND GrainId = @GrainId;

    IF FOUND_ROWS() = 0 THEN

        INSERT INTO OrleansGrainDirectory
        (
            ClusterId,
            ProviderId,
            GrainIdHash,
            GrainId,
            SiloAddress,
            ActivationId,
            CreatedOn
        )
        SELECT
            @ClusterId,
            @ProviderId,
            @GrainIdHash,
            @GrainId,
            @SiloAddress,
            @ActivationId,
            UTC_TIMESTAMP(3);

        SELECT
            ClusterId,
            ProviderId,
            GrainId,
            SiloAddress,
            ActivationId
        FROM
            OrleansGrainDirectory
        WHERE
            ClusterId = @ClusterId
            AND ProviderId = @ProviderId
            AND GrainIdHash = @GrainIdHash
            AND GrainId = @GrainId;

    END IF;

    COMMIT;
    UNLOCK TABLES;
    '
;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'UnregisterGrainActivationKey',
	'
    SET AUTOCOMMIT = 0;
    LOCK TABLES OrleansGrainDirectory WRITE;

    DELETE FROM OrleansGrainDirectory
    WHERE
        ClusterId = @ClusterId
        AND ProviderId = @ProviderId
        AND GrainIdHash = @GrainIdHash
        AND GrainId = @GrainId
        AND ActivationId = @ActivationId;

    SELECT ROW_COUNT();

    COMMIT;
    UNLOCK TABLES;
    '
;

DELIMITER $$

INSERT INTO OrleansQuery
(
    QueryKey,
    QueryText
)
SELECT
    'LookupGrainActivationKey',
    '
    SET AUTOCOMMIT = 0;
    LOCK TABLES OrleansGrainDirectory WRITE;

    SELECT
        ClusterId,
        ProviderId,
        GrainId,
        SiloAddress,
        ActivationId
    FROM
        OrleansGrainDirectory
    WHERE
        ClusterId = @ClusterId
        AND ProviderId = @ProviderId
        AND GrainIdHash = @GrainIdHash
        AND GrainId = @GrainId;

    COMMIT;
    UNLOCK TABLES;
    '
;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'UnregisterGrainActivationsKey',
	'
    CREATE TEMPORARY TABLE TempSiloAddresses
    (
        SiloAddress NVARCHAR(100) NOT NULL,
        Level INT NOT NULL
    );

    INSERT INTO TempSiloAddresses
    (
        SiloAddress,
        Level
    )
    WITH RECURSIVE SiloAddressesCTE AS
    (
        SELECT 
            SUBSTRING_INDEX(@SiloAddresses, ''|'', 1) AS Value,
  		    SUBSTRING(@SiloAddresses, CHAR_LENGTH(SUBSTRING_INDEX(@SiloAddresses, ''|'', 1)) + 2, CHAR_LENGTH(@SiloAddresses)) AS Others,
            1 AS Level
        UNION ALL
        SELECT 
            SUBSTRING_INDEX(Others, ''|'', 1) AS Value,
            SUBSTRING(Others, CHAR_LENGTH(SUBSTRING_INDEX(Others, ''|'', 1)) + 2, CHAR_LENGTH(Others)) AS Others,
            Level + 1
        FROM SiloAddressesCTE
        WHERE Others != ''''
    )
    SELECT Value, Level FROM SiloAddressesCTE;

    SET AUTOCOMMIT = 0;
    LOCK TABLE OrleansGrainDirectory WRITE;

    DELETE FROM OrleansGrainDirectory
    WHERE
        ClusterId = @ClusterId
        AND ProviderId = @ProviderId
        AND SiloAddress IN (SELECT SiloAddress FROM TempSiloAddresses);

    SELECT ROW_COUNT();

    COMMIT;
    UNLOCK TABLES;
    '
;