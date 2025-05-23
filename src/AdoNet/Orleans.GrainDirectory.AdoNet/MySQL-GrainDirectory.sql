/*
Orleans Grain Directory.

This table stores the location of all grains in the cluster.

The rationale for this table is as follows:

1. The table will see rows inserted individually, as new grains are added.
2. The table will see rows deleted at random as grains are deactivated, without regard for order.
3. Insert/Delete churn is expected to be very high.
4. The GrainId is VARCHAR(767) to support reasonable length grain identifiers.
*/
CREATE TABLE OrleansGrainDirectory
(
    /* Identifies the cluster instance */
    ClusterId NVARCHAR(150) NOT NULL,

    /* Identifies the grain directory provider */
    ProviderId NVARCHAR(150) NOT NULL,

    /* Holds the grain id in text form */
    GrainId VARCHAR(767) NOT NULL,

    /* Holds the silo address where the grain is located */
    SiloAddress NVARCHAR(100) NOT NULL,

    /* Holds the activation id in the silo where it is located */
    ActivationId NVARCHAR(100) NOT NULL,

    /* Holds the time at which the grain was added to the directory */
    CreatedOn DATETIME(3) NOT NULL,

    /* Primary key ensures uniqueness of grain identity */
    PRIMARY KEY (ClusterId, ProviderId, GrainId)
);

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT 'RegisterGrainActivationKey',
    '
    INSERT INTO OrleansGrainDirectory
    (
        ClusterId,
        ProviderId,
        GrainId,
        SiloAddress,
        ActivationId,
        CreatedOn
    )
    VALUES (
        @ClusterId,
        @ProviderId,
        @GrainId,
        @SiloAddress,
        @ActivationId,
        UTC_TIMESTAMP(3)
    )
    ON DUPLICATE KEY UPDATE
        ClusterId = ClusterId;

    -- Return the current registration
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
        AND GrainId = @GrainId;
    '
;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT 'UnregisterGrainActivationKey',
	'
    DELETE FROM OrleansGrainDirectory
    WHERE
        ClusterId = @ClusterId
        AND ProviderId = @ProviderId
        AND GrainId = @GrainId
        AND ActivationId = @ActivationId;

    SELECT ROW_COUNT() AS DeletedRows;
    '
;

DELIMITER $$

INSERT INTO OrleansQuery
(
    QueryKey,
    QueryText
)
SELECT 'LookupGrainActivationKey',
    '
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
        AND GrainId = @GrainId;
    '
;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT 'UnregisterGrainActivationsKey',
	'
    -- Parse silo addresses into temporary table for batch operation
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

    DELETE FROM OrleansGrainDirectory
    WHERE
        ClusterId = @ClusterId
        AND ProviderId = @ProviderId
        AND SiloAddress IN (SELECT SiloAddress FROM TempSiloAddresses);

    SELECT ROW_COUNT() AS DeletedRows;

    DROP TEMPORARY TABLE TempSiloAddresses;
    '
;
