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

    /* Holds the hash of the grain id */
    GrainIdHash INT NOT NULL,

    /* Holds the grain id in text form */
    GrainId NVARCHAR(MAX) NOT NULL,

    /* Holds the silo address where the grain is located */
    SiloAddress NVARCHAR(100) NOT NULL,

    /* Holds the activation id in the silo where it is located */
    ActivationId NVARCHAR(100) NOT NULL,

    /* Holds the time at which the grain was added to the directory */
    CreatedOn DATETIMEOFFSET(3) NOT NULL
);
GO

/*
This index is a workaround for the GrainId being too large to index by SQL Server.
Instead we index a stable hash of the GrainId.
Collisions are possible yet handled by careful use of locks on this index.
*/
CREATE NONCLUSTERED INDEX IX_OrleansGrainDirectory_Lookup
ON OrleansGrainDirectory
(
    ClusterId ASC,
    GrainIdHash ASC
);
GO

/* Prevent lock escalation to avoid accidental table locks on the grain directory */
ALTER TABLE OrleansGrainDirectory
SET (LOCK_ESCALATION = DISABLE);
GO

/* Registers a new grain activation */
CREATE PROCEDURE RegisterGrainActivation
    @ClusterId NVARCHAR(150),
    @GrainIdHash INT,
    @GrainId NVARCHAR(MAX),
    @SiloAddress NVARCHAR(100),
    @ActivationId NVARCHAR(100)
AS

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Now DATETIMEOFFSET(3) = CAST(SYSUTCDATETIME() AS DATETIMEOFFSET(3));

BEGIN TRANSACTION;

INSERT INTO OrleansGrainDirectory
(
    ClusterId,
    GrainIdHash,
    GrainId,
    SiloAddress,
    ActivationId,
    CreatedOn
)
SELECT
    @ClusterId,
    @GrainIdHash,
    @GrainId,
    @SiloAddress,
    @ActivationId,
    @Now
WHERE NOT EXISTS
(
    /* The lock on the index is used to prevent duplicates in place of a proper constraint */
    SELECT 1
    FROM OrleansGrainDirectory WITH (XLOCK, ROWLOCK, HOLDLOCK, INDEX(IX_OrleansGrainDirectory_Lookup))
    WHERE
        ClusterId = @ClusterId
        AND GrainIdHash = @GrainIdHash
        AND GrainId = @GrainId
)
OPTION (FAST 1, OPTIMIZE FOR (@ClusterId UNKNOWN, @GrainIdHash UNKNOWN, @GrainId UNKNOWN));

SELECT @@ROWCOUNT;

COMMIT;

GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'RegisterGrainActivationKey',
	'EXECUTE RegisterGrainActivation @ClusterId = @ClusterId, @GrainIdHash = @GrainIdHash, @GrainId = @GrainId, @SiloAddress = @SiloAddress, @ActivationId = @ActivationId'
GO

/* Unregisters an existing grain activation */
CREATE PROCEDURE UnregisterGrainActivation
    @ClusterId NVARCHAR(150),
    @GrainIdHash INT,
    @GrainId NVARCHAR(MAX),
    @ActivationId NVARCHAR(100)
AS

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DELETE OrleansGrainDirectory
FROM OrleansGrainDirectory WITH (XLOCK, ROWLOCK, HOLDLOCK, INDEX(IX_OrleansGrainDirectory_Lookup))
WHERE
    ClusterId = @ClusterId
    AND GrainIdHash = @GrainIdHash
    AND GrainId = @GrainId
    AND ActivationId = @ActivationId
OPTION (FAST 1, OPTIMIZE FOR (@ClusterId UNKNOWN, @GrainIdHash UNKNOWN, @GrainId UNKNOWN, @ActivationId UNKNOWN));

COMMIT;

GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'UnregisterGrainActivationKey',
	'EXECUTE UnregisterGrainActivation @ClusterId = @ClusterId, @GrainIdHash = @GrainIdHash, @GrainId = @GrainId, @ActivationId = @ActivationId'
GO


/* Looks up an existing grain activation */
CREATE PROCEDURE LookupGrainActivation
    @ClusterId NVARCHAR(150),
    @GrainIdHash INT,
    @GrainId NVARCHAR(MAX)
AS

SET NOCOUNT ON;
SET XACT_ABORT ON;

SELECT
    ClusterId,
    GrainId,
    SiloAddress,
    ActivationId
FROM
    OrleansGrainDirectory
WHERE
    ClusterId = @ClusterId
    AND GrainIdHash = @GrainIdHash
    AND GrainId = @GrainId
OPTION (FAST 1, OPTIMIZE FOR (@ClusterId UNKNOWN, @GrainIdHash UNKNOWN, @GrainId UNKNOWN));

GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'LookupGrainActivationKey',
	'EXECUTE LookupGrainActivation @ClusterId = @ClusterId, @GrainIdHash = @GrainIdHash, @GrainId = @GrainId'
GO


/* Unregisters all grain activations in the specified silos */
CREATE PROCEDURE UnregisterGrainActivations
    @ClusterId NVARCHAR(150),
    @SiloAddresses NVARCHAR(MAX)
AS

SET NOCOUNT ON;
SET XACT_ABORT ON;

DELETE OrleansGrainDirectory
FROM OrleansGrainDirectory WITH (TABLOCKX)
WHERE
    ClusterId = @ClusterId
    AND SiloAddress IN (SELECT Value FROM STRING_SPLIT(@SiloAddresses, '|'));

SELECT @@ROWCOUNT;

GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'UnregisterGrainActivationsKey',
	'EXECUTE UnregisterGrainActivations @ClusterId = @ClusterId, @SiloAddresses = @SiloAddresses'
GO
