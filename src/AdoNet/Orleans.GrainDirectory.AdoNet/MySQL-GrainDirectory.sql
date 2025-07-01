/*
Orleans Grain Directory.
This table stores the location of all grains in the cluster.

NOTE:
The combination of ClusterId, ProviderId, and GrainId forms the primary key for the OrleansGrainDirectory table.
Together, these columns reach the maximum allowed key size for MariaDB/MySQL indexes (768 bytes).
Care should be taken not to increase the length of these columns, as it may exceed MariaDB/MySQL's key size limitation.

*/
CREATE TABLE OrleansGrainDirectory
(
    /* Identifies the cluster instance */
    ClusterId VARCHAR(150) NOT NULL,

    /* Identifies the grain directory provider */
    ProviderId VARCHAR(150) NOT NULL,

    /* Holds the grain id in text form */
    GrainId VARCHAR(468) NOT NULL,

    /* Holds the silo address where the grain is located */
    SiloAddress VARCHAR(100) NOT NULL,

    /* Holds the activation id in the silo where it is located */
    ActivationId VARCHAR(100) NOT NULL,

    /* Holds the time at which the grain was added to the directory */
    CreatedOn DATETIME(3) NOT NULL,

    /* Primary key ensures uniqueness of grain identity */
    PRIMARY KEY (ClusterId, ProviderId, GrainId)
);

DELIMITER $$

CREATE PROCEDURE RegisterGrainActivation
(
    IN _ClusterId NVARCHAR(150),
    IN _ProviderId NVARCHAR(150),
    IN _GrainId NVARCHAR(468),
    IN _SiloAddress NVARCHAR(100),
    IN _ActivationId NVARCHAR(100)
)
BEGIN

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;

    START TRANSACTION;

    INSERT INTO OrleansGrainDirectory
    (
        ClusterId,
        ProviderId,
        GrainId,
        SiloAddress,
        ActivationId,
        CreatedOn
    )
    VALUES
    (
        _ClusterId,
        _ProviderId,
        _GrainId,
        _SiloAddress,
        _ActivationId,
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
        ClusterId = _ClusterId
        AND ProviderId = _ProviderId
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
    'RegisterGrainActivationKey',
    'CALL RegisterGrainActivation(@ClusterId, @ProviderId, @GrainId, @SiloAddress, @ActivationId);';

DELIMITER $$

CREATE PROCEDURE UnregisterGrainActivation
(
    IN _ClusterId VARCHAR(150),
    IN _ProviderId VARCHAR(150),
    IN _GrainId VARCHAR(468),
    IN _ActivationId VARCHAR(100)
)
BEGIN
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;

    START TRANSACTION;

    DELETE FROM OrleansGrainDirectory
    WHERE
        ClusterId = _ClusterId
        AND ProviderId = _ProviderId
        AND GrainId = _GrainId
        AND ActivationId = _ActivationId;

    SELECT ROW_COUNT() AS DeletedRows;

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
	'CALL UnregisterGrainActivation(@ClusterId, @ProviderId, @GrainId, @ActivationId);';

DELIMITER $$

CREATE PROCEDURE LookupGrainActivation
(
    IN _ClusterId VARCHAR(150),
    IN _ProviderId VARCHAR(150),
    IN _GrainId VARCHAR(468)
)
BEGIN
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
END

DELIMITER $$

INSERT INTO OrleansQuery
(
    QueryKey,
    QueryText
)
SELECT
    'LookupGrainActivationKey',
    'CALL LookupGrainActivation(@ClusterId, @ProviderId, @GrainId);';

DELIMITER $$

CREATE PROCEDURE UnregisterGrainActivations
(
    IN _ClusterId VARCHAR(150),
    IN _ProviderId VARCHAR(150),
    IN _SiloAddresses TEXT
)
BEGIN
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;

    START TRANSACTION;

    -- Parse silo addresses into temporary table for batch operation
    CREATE TEMPORARY TABLE TempSiloAddresses
    (
        SiloAddress VARCHAR(100) NOT NULL,
        Level INT NOT NULL
    );

    INSERT INTO TempSiloAddresses (SiloAddress, Level)
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

    DELETE FROM OrleansGrainDirectory
    WHERE
        ClusterId = _ClusterId
        AND ProviderId = _ProviderId
        AND SiloAddress IN (SELECT SiloAddress FROM TempSiloAddresses);

    SELECT ROW_COUNT() AS DeletedRows;

    DROP TEMPORARY TABLE TempSiloAddresses;

    COMMIT;
END

DELIMITER $$

INSERT INTO OrleansQuery
(
	QueryKey,
	QueryText
)
SELECT
    'UnregisterGrainActivationsKey',
    'CALL UnregisterGrainActivations(@ClusterId, @ProviderId, @SiloAddresses);';

DELIMITER ;
