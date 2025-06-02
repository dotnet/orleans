/*
Orleans Grain Directory.
This table stores the location of all grains in the cluster.

NOTE:
The combination of ClusterId, ProviderId, and GrainId forms the primary key for the OrleansGrainDirectory table.
Together, these columns reach the maximum allowed key size for SQL Server indexes (900 bytes).
Care should be taken not to increase the length of these columns, as it may exceed SQL Server's key size limitation.

*/
CREATE TABLE OrleansGrainDirectory
(
    /* Identifies the cluster instance */
    ClusterId VARCHAR(150) NOT NULL,

    /* Identifies the grain directory provider */
    ProviderId VARCHAR(150) NOT NULL,

    /* Holds the grain id in text form */
    GrainId VARCHAR(600) NOT NULL,

    /* Holds the silo address where the grain is located */
    SiloAddress VARCHAR(100) NOT NULL,

    /* Holds the activation id in the silo where it is located */
    ActivationId VARCHAR(100) NOT NULL,

    /* Holds the time at which the grain was added to the directory */
    CreatedOn DATETIMEOFFSET(3) NOT NULL,

    /* Identifies a unique grain activation */
    CONSTRAINT PK_OrleansGrainDirectory PRIMARY KEY CLUSTERED
    (
        ClusterId ASC,
        ProviderId ASC,
        GrainId ASC
    )
)
GO

/* Registers a new grain activation */
CREATE PROCEDURE RegisterGrainActivation
    @ClusterId VARCHAR(150),
    @ProviderId VARCHAR(150),
    @GrainId VARCHAR(600),
    @SiloAddress VARCHAR(100),
    @ActivationId VARCHAR(100)
AS

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Now DATETIMEOFFSET(3) = CAST(SYSUTCDATETIME() AS DATETIMEOFFSET(3));

BEGIN TRANSACTION;

/* Get the existing entry if it exists. */
/* This also places a lock on the range upfront in case we need to add a new entry. */
/* This lock is required to prevent deadlocks. */
DECLARE @Exists INT =
(
    SELECT
        1
    FROM
        OrleansGrainDirectory WITH (UPDLOCK, HOLDLOCK)
    WHERE
        ClusterId = @ClusterId
        AND ProviderId = @ProviderId
        AND GrainId = @GrainId
);

IF @Exists = 1
BEGIN

    /* If the entry already exists, we can return it. */
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

END
ELSE
BEGIN

    /* If the entry does not yet exist, we will add it now. */
    INSERT INTO OrleansGrainDirectory
    (
        ClusterId,
        ProviderId,
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
    VALUES
    (
        @ClusterId,
        @ProviderId,
        @GrainId,
        @SiloAddress,
        @ActivationId,
        @Now
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
	'EXECUTE RegisterGrainActivation @ClusterId = @ClusterId, @ProviderId = @ProviderId, @GrainId = @GrainId, @SiloAddress = @SiloAddress, @ActivationId = @ActivationId'
GO

/* Unregisters an existing grain activation */
CREATE PROCEDURE UnregisterGrainActivation
    @ClusterId VARCHAR(150),
    @ProviderId VARCHAR(150),
    @GrainId VARCHAR(600),
    @ActivationId VARCHAR(100)
AS

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* Delete the entry if it exists. */
/* This places a lock on the hash index pages upfront to prevent deadlocks. */
DELETE OrleansGrainDirectory
FROM OrleansGrainDirectory
WHERE
    ClusterId = @ClusterId
    AND ProviderId = @ProviderId
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
	'EXECUTE UnregisterGrainActivation @ClusterId = @ClusterId, @ProviderId = @ProviderId, @GrainId = @GrainId, @ActivationId = @ActivationId'
GO


/* Looks up an existing grain activation */
CREATE PROCEDURE LookupGrainActivation
    @ClusterId VARCHAR(150),
    @ProviderId VARCHAR(150),
    @GrainId VARCHAR(600)
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
    OrleansGrainDirectory
WHERE
    ClusterId = @ClusterId
    AND ProviderId = @ProviderId
    AND GrainId = @GrainId;

GO

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'LookupGrainActivationKey',
	'EXECUTE LookupGrainActivation @ClusterId = @ClusterId, @ProviderId = @ProviderId, @GrainId = @GrainId'
GO


/* Unregisters all grain activations in the specified silos */
CREATE PROCEDURE UnregisterGrainActivations
    @ClusterId VARCHAR(150),
    @ProviderId VARCHAR(150),
    @SiloAddresses VARCHAR(MAX)
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
