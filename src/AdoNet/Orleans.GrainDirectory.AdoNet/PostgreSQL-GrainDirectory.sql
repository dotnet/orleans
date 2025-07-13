/*
Orleans Grain Directory.
This table stores the location of all grains in the cluster.

NOTE:
The combination of ClusterId, ProviderId, and GrainId forms the primary key for the OrleansGrainDirectory table.
Together, these columns reach the maximum allowed key size for PostgreSQL indexes (2704 bytes).
Care should be taken not to increase the length of these columns, as it may exceed PostgreSQL's key size limitation.

*/
CREATE TABLE OrleansGrainDirectory
(
    /* Identifies the cluster instance */
    ClusterId VARCHAR(150) NOT NULL,

    /* Identifies the grain directory provider */
    ProviderId VARCHAR(150) NOT NULL,

    /* Holds the grain id in text form */
    GrainId VARCHAR(2404) NOT NULL,

    /* Holds the silo address where the grain is located */
    SiloAddress VARCHAR(100) NOT NULL,

    /* Holds the activation id in the silo where it is located */
    ActivationId VARCHAR(100) NOT NULL,

    /* Holds the time at which the grain was added to the directory */
    CreatedOn TIMESTAMPTZ NOT NULL,

    /* Identifies a unique grain activation */
    CONSTRAINT PK_OrleansGrainDirectory PRIMARY KEY
    (
        ClusterId,
        ProviderId,
        GrainId
    )
);

/* Registers a new grain activation */
CREATE OR REPLACE FUNCTION RegisterGrainActivation(
    _ClusterId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _GrainId VARCHAR(600),
    _SiloAddress VARCHAR(100),
    _ActivationId VARCHAR(100)
)
RETURNS TABLE
(
    ClusterId VARCHAR(150),
    ProviderId VARCHAR(150),
    GrainId VARCHAR(600),
    SiloAddress VARCHAR(100),
    ActivationId VARCHAR(100)
)
LANGUAGE plpgsql
AS $$
#VARIABLE_CONFLICT USE_COLUMN
DECLARE
    _Now TIMESTAMPTZ := NOW();
BEGIN

RETURN QUERY
INSERT INTO OrleansGrainDirectory
(
    ClusterId,
    ProviderId,
    GrainId,
    SiloAddress,
    ActivationId,
    CreatedOn
)
SELECT
    _ClusterId,
    _ProviderId,
    _GrainId,
    _SiloAddress,
    _ActivationId,
    _Now
ON CONFLICT (ClusterId, ProviderId, GrainId)
DO UPDATE SET
    ClusterId = _ClusterId,
    ProviderId = _ProviderId,
    GrainId = _GrainId
RETURNING
    ClusterId,
    ProviderId,
    GrainId,
    SiloAddress,
    ActivationId;

END;
$$;

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
	'RegisterGrainActivationKey',
	'START TRANSACTION; SELECT * FROM RegisterGrainActivation (@ClusterId, @ProviderId, @GrainId, @SiloAddress, @ActivationId); COMMIT;'
;

/* Unregisters an existing grain activation */
CREATE OR REPLACE FUNCTION UnregisterGrainActivation(
    _ClusterId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _GrainId VARCHAR(600),
    _ActivationId VARCHAR(100)
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    _RowCount INT;
BEGIN

DELETE FROM OrleansGrainDirectory
WHERE ClusterId = _ClusterId
    AND ProviderId = _ProviderId
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
	'SELECT * FROM UnregisterGrainActivation (@ClusterId, @ProviderId, @GrainId, @ActivationId)'
;

/* Looks up an existing grain activation */
CREATE OR REPLACE FUNCTION LookupGrainActivation(
    _ClusterId VARCHAR(150),
    _ProviderId VARCHAR(150),
    _GrainId VARCHAR(600)
)
RETURNS TABLE
(
    ClusterId VARCHAR(150),
    ProviderId VARCHAR(150),
    GrainId VARCHAR(600),
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
    'SELECT * FROM LookupGrainActivation(@ClusterId, @ProviderId, @GrainId)'
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
