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

/* Registers a new grain activation */
CREATE PROCEDURE RegisterGrainActivation
(
    IN _ClusterId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _GrainIdHash INT,
    IN _GrainId TEXT,
    IN _SiloAddress NVARCHAR(100),
    IN _ActivationId NVARCHAR(100)
)
BEGIN

DECLARE _Now DATETIME(3) DEFAULT UTC_TIMESTAMP(3);

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
START TRANSACTION;

SELECT
    ClusterId,
    ProviderId,
    GrainId,
    SiloAddress,
    ActivationId
FROM
    OrleansGrainDirectory
WHERE
    ClusterId = _ClusterId
    AND ProviderId = _ProviderId
    AND GrainIdHash = _GrainIdHash
    AND GrainId = _GrainId
FOR UPDATE;

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
    VALUES
    (
        _ClusterId,
        _ProviderId,
        _GrainIdHash,
        _GrainId,
        _SiloAddress,
        _ActivationId,
        _Now
    );

    SELECT
        ClusterId,
        ProviderId,
        GrainId,
        SiloAddress,
        ActivationId
    FROM
        OrleansGrainDirectory
    WHERE
        ClusterId = _ClusterId
        AND ProviderId = _ProviderId
        AND GrainIdHash = _GrainIdHash
        AND GrainId = _GrainId;

END IF;

COMMIT;

END;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'RegisterGrainActivationKey',
	'CALL RegisterGrainActivation (@ClusterId, @ProviderId, @GrainIdHash, @GrainId, @SiloAddress, @ActivationId)'
;

DELIMITER $$

/* Unregisters an existing grain activation */
CREATE PROCEDURE UnregisterGrainActivation
(
    IN _ClusterId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _GrainIdHash INT,
    IN _GrainId TEXT,
    IN _ActivationId NVARCHAR(100)
)
BEGIN

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
START TRANSACTION;

CREATE TEMPORARY TABLE _Batch AS
SELECT
    *
FROM
    OrleansGrainDirectory
WHERE
    ClusterId = _ClusterId
    AND ProviderId = _ProviderId
    AND GrainIdHash = _GrainIdHash
    AND GrainId = _GrainId
FOR UPDATE;

DELETE FROM OrleansGrainDirectory
WHERE ClusterId = _ClusterId
    AND ProviderId = _ProviderId
    AND GrainIdHash = _GrainIdHash
    AND GrainId = _GrainId
    AND ActivationId = _ActivationId;

SELECT ROW_COUNT();

DROP TEMPORARY TABLE _Batch;

COMMIT;

END;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'UnregisterGrainActivationKey',
	'CALL UnregisterGrainActivation (@ClusterId, @ProviderId, @GrainIdHash, @GrainId, @ActivationId)'
;

DELIMITER $$

/* Looks up an existing grain activation */
CREATE PROCEDURE LookupGrainActivation
(
    IN _ClusterId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _GrainIdHash INT,
    IN _GrainId TEXT
)
BEGIN

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
START TRANSACTION;

SELECT
    ClusterId,
    ProviderId,
    GrainId,
    SiloAddress,
    ActivationId
FROM
    OrleansGrainDirectory
WHERE
    ClusterId = _ClusterId
    AND ProviderId = _ProviderId
    AND GrainIdHash = _GrainIdHash
    AND GrainId = _GrainId;

COMMIT;

END;

DELIMITER $$

INSERT INTO OrleansQuery
(
    QueryKey,
    QueryText
)
SELECT
    'LookupGrainActivationKey',
    'CALL LookupGrainActivation(@ClusterId, @ProviderId, @GrainIdHash, @GrainId)'
;

DELIMITER $$

/* Unregisters all grain activations in the specified silos */
CREATE PROCEDURE UnregisterGrainActivations
(
    IN _ClusterId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _SiloAddresses TEXT
)
BEGIN

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
        SUBSTRING_INDEX(_SiloAddresses, '|', 1) AS Value,
  		SUBSTRING(_SiloAddresses, CHAR_LENGTH(SUBSTRING_INDEX(_SiloAddresses, '|', 1)) + 2, CHAR_LENGTH(_SiloAddresses)) AS Others,
        1 AS Level
    UNION ALL
    SELECT 
        SUBSTRING_INDEX(Others, '|', 1) AS Value,
        SUBSTRING(Others, CHAR_LENGTH(SUBSTRING_INDEX(Others, '|', 1)) + 2, CHAR_LENGTH(Others)) AS Others,
        Level + 1
    FROM SiloAddressesCTE
    WHERE Others != ''
)
SELECT Value, Level FROM SiloAddressesCTE;

SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
START TRANSACTION;

DELETE FROM OrleansGrainDirectory
WHERE
    ClusterId = _ClusterId
    AND ProviderId = _ProviderId
    AND SiloAddress IN (SELECT SiloAddress FROM TempSiloAddresses);

SELECT ROW_COUNT();

COMMIT;

END;

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'UnregisterGrainActivationsKey',
	'CALL UnregisterGrainActivations (@ClusterId, @ProviderId, @SiloAddresses)'
;