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
    CreatedOn DATETIMEOFFSET(3) NOT NULL,
)
GO

/* This turns the table into a CLUSTERED INDEX that allows duplication on the hash. */
CREATE CLUSTERED INDEX CI_OrleansGrainDirectory
ON OrleansGrainDirectory
(
    ClusterId ASC,
    ProviderId ASC,
    GrainIdHash ASC
)
GO

ALTER TABLE OrleansGrainDirectory
SET (LOCK_ESCALATION = DISABLE)
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

/* Get the existing entry if it exists. */
/* This also places a lock on the hash index pages upfront in case we need to add a new entry. */
/* This lock is required to prevent both deadlocks and duplicates. */
SELECT
    ClusterId,
    ProviderId,
    GrainId,
    SiloAddress,
    ActivationId
FROM
    OrleansGrainDirectory WITH (UPDLOCK, PAGLOCK, HOLDLOCK, INDEX(CI_OrleansGrainDirectory))
WHERE
    ClusterId = @ClusterId
    AND ProviderId = @ProviderId
    AND GrainIdHash = @GrainIdHash
    AND GrainId = @GrainId;

/* Otherwise add a new entry if one does exist yet. */
IF @@ROWCOUNT = 0
BEGIN

    MERGE INTO OrleansGrainDirectory WITH (UPDLOCK, PAGLOCK, HOLDLOCK, INDEX(CI_OrleansGrainDirectory)) AS Target
    USING
    (
        SELECT
            @ClusterId AS ClusterId,
            @ProviderId AS ProviderId,
            @GrainIdHash AS GrainIdHash,
            @GrainId AS GrainId,
            @SiloAddress AS SiloAddress,
            @ActivationId AS ActivationId,
            @Now AS CreatedOn
    ) AS Source
    ON
        Target.ClusterId = Source.ClusterId
        AND Target.ProviderId = Source.ProviderId
        AND Target.GrainIdHash = Source.GrainIdHash
        AND Target.GrainId = Source.GrainId
    WHEN NOT MATCHED BY TARGET THEN
        INSERT
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
            Source.ClusterId,
            Source.ProviderId,
            Source.GrainIdHash,
            Source.GrainId,
            Source.SiloAddress,
            Source.ActivationId,
            Source.CreatedOn
        )
    OUTPUT
        INSERTED.ClusterId,
        INSERTED.ProviderId,
        INSERTED.GrainId,
        INSERTED.SiloAddress,
        INSERTED.ActivationId;

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

/* Delete the entry if it exists. */
/* This places a lock on the hash index pages upfront to prevent deadlocks. */
DELETE OrleansGrainDirectory
FROM OrleansGrainDirectory WITH (UPDLOCK, PAGLOCK, HOLDLOCK, INDEX(CI_OrleansGrainDirectory))
WHERE
    ClusterId = @ClusterId
    AND ProviderId = @ProviderId
    AND GrainIdHash = @GrainIdHash
    AND GrainId = @GrainId
    AND ActivationId = @ActivationId;

SELECT @@ROWCOUNT;

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

/* Get the existing entry if it exists. */
/* This also places a lock on the hash index pages upfront to prevent deadlocks with registration. */
SELECT
    ClusterId,
    ProviderId,
    GrainId,
    SiloAddress,
    ActivationId
FROM
    OrleansGrainDirectory WITH (UPDLOCK, PAGLOCK, HOLDLOCK, INDEX(CI_OrleansGrainDirectory))
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

/* Delete the entries if they exist. */
/* This places a exclusive lock on the entire table to prevent deadlocks with registration. */
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
