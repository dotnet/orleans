/*
Orleans Grain Directory.

This table stores the location of all grains in the cluster.

The demands for this table are as follows:

1. The table will see rows inserted individually, as new grains are added.
2. The table will see rows deleted at random as grains are deactivated, without regard for order.
3. Insert/Delete churn is expected to be very high.
4. The GrainId is too large to be indexed by SQL Server.

To address these demands, this table is implemented as a non-unique table with a hash index.
This index covers the hash of the grain id to allow for fast lookups.
Uniqueness is then guaranteed by careful use of locks on the hash index.

*/
CREATE TABLE OrleansGrainDirectory
(
    /* Identifies the cluster instance */
    ClusterId VARCHAR(150) NOT NULL,

    /* Identifies the grain directory provider */
    ProviderId VARCHAR(150) NOT NULL,

    /* Holds the hash of the grain id */
    GrainIdHash INT NOT NULL,

    /* Holds the grain id in text form */
    GrainId TEXT NOT NULL,

    /* Holds the silo address where the grain is located */
    SiloAddress VARCHAR(100) NOT NULL,

    /* Holds the activation id in the silo where it is located */
    ActivationId VARCHAR(100) NOT NULL,

    /* Holds the time at which the grain was added to the directory */
    CreatedOn TIMESTAMPTZ NOT NULL
);

/* This turns the table into a CLUSTERED INDEX that allows duplication on the hash. */
CREATE INDEX IX_OrleansGrainDirectory
ON OrleansGrainDirectory
(
    ClusterId,
    ProviderId,
    GrainIdHash
);

/* Registers a new grain activation */
CREATE OR REPLACE FUNCTION RegisterGrainActivation(
    _ClusterId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _GrainIdHash INT,
    _GrainId TEXT,
    _SiloAddress VARCHAR(100),
    _ActivationId VARCHAR(100)
)
RETURNS TABLE
(
    ClusterId VARCHAR(150),
    ProviderId VARCHAR(150),
    GrainId TEXT,
    SiloAddress VARCHAR(100),
    ActivationId VARCHAR(100)
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMPTZ := NOW();
BEGIN

-- this is required to prevent both duplication and deadlocks
LOCK TABLE OrleansGrainDirectory IN EXCLUSIVE MODE;

RETURN QUERY
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

IF NOT FOUND THEN

    RETURN QUERY
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
    )
    RETURNING
        ClusterId,
        ProviderId,
        GrainId,
        SiloAddress,
        ActivationId;

END IF;

END;
$$;

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'RegisterGrainActivationKey',
	'SELECT * FROM RegisterGrainActivation (@ClusterId, @ProviderId, @GrainIdHash, @GrainId, @SiloAddress, @ActivationId)'
;

/* Unregisters an existing grain activation */
CREATE OR REPLACE FUNCTION UnregisterGrainActivation(
    _ClusterId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _GrainIdHash INT,
    _GrainId TEXT,
    _ActivationId VARCHAR(100)
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    _RowCount INT;
BEGIN

-- this is required to prevent both duplication and deadlocks
LOCK TABLE OrleansGrainDirectory IN EXCLUSIVE MODE;

DELETE FROM OrleansGrainDirectory
WHERE ClusterId = _ClusterId
    AND ProviderId = _ProviderId
    AND GrainIdHash = _GrainIdHash
    AND GrainId = _GrainId
    AND ActivationId = _ActivationId;

GET DIAGNOSTICS _RowCount = ROW_COUNT;

RETURN _RowCount;

END;
$$;

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'UnregisterGrainActivationKey',
	'SELECT * FROM UnregisterGrainActivation (@ClusterId, @ProviderId, @GrainIdHash, @GrainId, @ActivationId)'
;

/* Looks up an existing grain activation */
CREATE OR REPLACE FUNCTION LookupGrainActivation(
    _ClusterId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _GrainIdHash INT,
    _GrainId TEXT
)
RETURNS TABLE
(
    ClusterId VARCHAR(150),
    ProviderId VARCHAR(150),
    GrainId TEXT,
    SiloAddress VARCHAR(100),
    ActivationId VARCHAR(100)
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
BEGIN

RETURN QUERY
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

END;
$$;

INSERT INTO OrleansQuery
(
    QueryKey,
    QueryText
)
SELECT
    'LookupGrainActivationKey',
    'SELECT * FROM LookupGrainActivation(@ClusterId, @ProviderId, @GrainIdHash, @GrainId)'
;

/* Unregisters all grain activations in the specified silos */
CREATE OR REPLACE FUNCTION UnregisterGrainActivations(
    _ClusterId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _SiloAddresses TEXT
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    _RowCount INT;
BEGIN

-- this is required to prevent both duplication and deadlocks
LOCK TABLE OrleansGrainDirectory IN EXCLUSIVE MODE;

DELETE FROM OrleansGrainDirectory
WHERE
    ClusterId = _ClusterId
    AND ProviderId = _ProviderId
    AND SiloAddress = ANY (string_to_array(_SiloAddresses, '|'));

GET DIAGNOSTICS _RowCount = ROW_COUNT;

RETURN _RowCount;

END;
$$;

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'UnregisterGrainActivationsKey',
	'SELECT * FROM UnregisterGrainActivations (@ClusterId, @ProviderId, @SiloAddresses)'
;