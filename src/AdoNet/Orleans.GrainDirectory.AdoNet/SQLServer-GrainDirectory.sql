/*
Orleans Grain Directory.

This table stores the location of all grains in the cluster.

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
    GrainId NVARCHAR(MAX) NOT NULL,

    /* Holds the silo address where the grain is located */
    SiloAddress NVARCHAR(100) NOT NULL,

    /* Holds the activation id in the silo where it is located */
    ActivationId NVARCHAR(100) NOT NULL,

    /* Holds the time at which the grain was added to the directory */
    CreatedOn DATETIMEOFFSET(3) NOT NULL
)
GO

ALTER TABLE OrleansGrainDirectory
SET (LOCK_ESCALATION = DISABLE)
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
    ProviderId ASC,
    GrainIdHash ASC
)
INCLUDE
(
    GrainId,
    SiloAddress,
    ActivationId,
    CreatedOn
)
GO

/* Registers a new grain activation */
CREATE PROCEDURE RegisterGrainActivation
    @ClusterId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @GrainIdHash INT,
    @GrainId NVARCHAR(MAX),
    @SiloAddress NVARCHAR(100),
    @ActivationId NVARCHAR(100)
AS

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Now DATETIMEOFFSET(3) = CAST(SYSUTCDATETIME() AS DATETIMEOFFSET(3));

BEGIN TRANSACTION;

/* First we check if the entry already exists. */
/* This also induces and holds a lock on the hash index upfront to prevent both duplicates and deadlocks. */
/* This is also required to ensure the server always locks the index before it locks the underlying table upon modification. */
SELECT
    ClusterId,
    ProviderId,
    GrainId,
    SiloAddress,
    ActivationId
FROM
    OrleansGrainDirectory WITH (UPDLOCK, PAGLOCK, HOLDLOCK, INDEX(IX_OrleansGrainDirectory_Lookup))
WHERE
    ClusterId = @ClusterId
    AND ProviderId = @ProviderId
    AND GrainIdHash = @GrainIdHash
    AND GrainId = @GrainId;

/* If no current entry was found we can add a new one now. */
IF @@ROWCOUNT = 0
BEGIN
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
    OUTPUT
        INSERTED.ClusterId,
        INSERTED.ProviderId,
        INSERTED.GrainId,
        INSERTED.SiloAddress,
        INSERTED.ActivationId
    SELECT
        @ClusterId,
        @ProviderId,
        @GrainIdHash,
        @GrainId,
        @SiloAddress,
        @ActivationId,
        @Now

    /* This check should not be required given we are already holding a lock on the hash. */
    /* However it is included here as an extra safety measure. */
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM OrleansGrainDirectory WITH (UPDLOCK, PAGLOCK, HOLDLOCK, INDEX(IX_OrleansGrainDirectory_Lookup))
        WHERE
            ClusterId = @ClusterId
            AND ProviderId = @ProviderId
            AND GrainIdHash = @GrainIdHash
            AND GrainId = @GrainId
    );
END

COMMIT;

GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'RegisterGrainActivationKey',
	'EXECUTE RegisterGrainActivation @ClusterId = @ClusterId, @ProviderId = @ProviderId, @GrainIdHash = @GrainIdHash, @GrainId = @GrainId, @SiloAddress = @SiloAddress, @ActivationId = @ActivationId'
GO

/* Unregisters an existing grain activation */
CREATE PROCEDURE UnregisterGrainActivation
    @ClusterId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @GrainIdHash INT,
    @GrainId NVARCHAR(MAX),
    @ActivationId NVARCHAR(100)
AS

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

/* Induce a lock on the hash index upfront to prevent both duplicates and deadlocks. */
/* This is required to ensure the server always locks the index before it locks the underlying table upon modification. */
DECLARE @Locked INT =
(
    SELECT COUNT(*)
    FROM OrleansGrainDirectory WITH (UPDLOCK, PAGLOCK, HOLDLOCK, INDEX(IX_OrleansGrainDirectory_Lookup))
    WHERE
        ClusterId = @ClusterId
        AND ProviderId = @ProviderId
        AND GrainIdHash = @GrainIdHash
        AND GrainId = @GrainId
);

/* It is now safe to remove the entry. */
DELETE OrleansGrainDirectory
FROM OrleansGrainDirectory WITH (UPDLOCK, PAGLOCK, HOLDLOCK, INDEX(IX_OrleansGrainDirectory_Lookup))
WHERE
    ClusterId = @ClusterId
    AND ProviderId = @ProviderId
    AND GrainIdHash = @GrainIdHash
    AND GrainId = @GrainId
    AND ActivationId = @ActivationId;

SELECT @@ROWCOUNT;

COMMIT;

GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'UnregisterGrainActivationKey',
	'EXECUTE UnregisterGrainActivation @ClusterId = @ClusterId, @ProviderId = @ProviderId, @GrainIdHash = @GrainIdHash, @GrainId = @GrainId, @ActivationId = @ActivationId'
GO


/* Looks up an existing grain activation */
CREATE PROCEDURE LookupGrainActivation
    @ClusterId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @GrainIdHash INT,
    @GrainId NVARCHAR(MAX)
AS

SET NOCOUNT ON;
SET XACT_ABORT ON;

SELECT
    ClusterId,
    ProviderId,
    GrainId,
    SiloAddress,
    ActivationId
FROM
    OrleansGrainDirectory WITH (UPDLOCK, PAGLOCK, HOLDLOCK, INDEX(IX_OrleansGrainDirectory_Lookup))
WHERE
    ClusterId = @ClusterId
    AND ProviderId = @ProviderId
    AND GrainIdHash = @GrainIdHash
    AND GrainId = @GrainId;

GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'LookupGrainActivationKey',
	'EXECUTE LookupGrainActivation @ClusterId = @ClusterId, @ProviderId = @ProviderId, @GrainIdHash = @GrainIdHash, @GrainId = @GrainId'
GO


/* Unregisters all grain activations in the specified silos */
CREATE PROCEDURE UnregisterGrainActivations
    @ClusterId NVARCHAR(150),
    @ProviderId NVARCHAR(150),
    @SiloAddresses NVARCHAR(MAX)
AS

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DELETE OrleansGrainDirectory
FROM OrleansGrainDirectory WITH (TABLOCKX)
WHERE
    ClusterId = @ClusterId
    AND ProviderId = @ProviderId
    AND SiloAddress IN (SELECT Value FROM STRING_SPLIT(@SiloAddresses, '|'));

SELECT @@ROWCOUNT;

COMMIT TRANSACTION;

GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'UnregisterGrainActivationsKey',
	'EXECUTE UnregisterGrainActivations @ClusterId = @ClusterId, @ProviderId = @ProviderId, @SiloAddresses = @SiloAddresses'
GO
