-- Updates membership writes and adds captured-value Dead-row pruning.
-- Apply to an existing clustering database before starting the updated provider.
-- Existing table schemas, query parameters, and routine signatures are preserved.
-- Create the new routine once, grant runtime callers EXECUTE, then publish the queries below.
-- The existing InsertMembershipKey routine and its grants remain available to cached callers.

DELIMITER $$

CREATE PROCEDURE InsertMembershipKeyAtomic(
    in    _DeploymentId NVARCHAR(150),
    in    _Address VARCHAR(45),
    in    _Port INT,
    in    _Generation INT,
    in    _Version INT,
    in    _SiloName NVARCHAR(150),
    in    _HostName NVARCHAR(150),
    in    _Status INT,
    in    _ProxyPort INT,
    in    _StartTime DATETIME,
    in    _IAmAliveTime DATETIME
)
BEGIN
    DECLARE _ROWCOUNT INT;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    START TRANSACTION;

    UPDATE OrleansMembershipVersionTable
    SET Version = Version + 1
    WHERE DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
        AND Version = _Version AND _Version IS NOT NULL AND Version < 2147483647;
    SET _ROWCOUNT = ROW_COUNT();

    INSERT INTO OrleansMembershipTable
    (
        DeploymentId,
        Address,
        Port,
        Generation,
        SiloName,
        HostName,
        Status,
        ProxyPort,
        StartTime,
        IAmAliveTime
    )
    SELECT * FROM ( SELECT
        _DeploymentId,
        _Address,
        _Port,
        _Generation,
        _SiloName,
        _HostName,
        _Status,
        _ProxyPort,
        _StartTime,
        _IAmAliveTime) AS TMP
    WHERE _ROWCOUNT > 0 AND NOT EXISTS
    (
    SELECT 1
    FROM
        OrleansMembershipTable
    WHERE
        DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
        AND Address = _Address AND _Address IS NOT NULL
        AND Port = _Port AND _Port IS NOT NULL
        AND Generation = _Generation AND _Generation IS NOT NULL
    );

    SET _ROWCOUNT = ROW_COUNT();

    IF _ROWCOUNT = 0
    THEN
        ROLLBACK;
    ELSE
        COMMIT;
    END IF;
    SELECT _ROWCOUNT;
END$$

DELIMITER ;

-- Ensure runtime callers can execute InsertMembershipKeyAtomic before publishing this catalog.
START TRANSACTION;

UPDATE OrleansQuery SET QueryText = '
    call InsertMembershipKeyAtomic(@DeploymentId, @Address, @Port, @Generation,
    @Version, @SiloName, @HostName, @Status, @ProxyPort, @StartTime, @IAmAliveTime);'
WHERE QueryKey = 'InsertMembershipKey';

UPDATE OrleansQuery SET QueryText = '
    -- This is expected to never fail by Orleans, so return value
    -- is not needed nor is it checked.
    UPDATE OrleansMembershipTable
    SET
        IAmAliveTime = @IAmAliveTime
    WHERE
        DeploymentId = @DeploymentId
        AND Address = @Address
        AND Port = @Port
        AND Generation = @Generation;
'
WHERE QueryKey = 'UpdateIAmAlivetimeKey';

UPDATE OrleansQuery SET QueryText = '
    UPDATE OrleansMembershipVersionTable v
    INNER JOIN OrleansMembershipTable m ON m.DeploymentId = v.DeploymentId
    SET v.Version = v.Version + 1,
        m.Status = @Status,
        m.SuspectTimes = @SuspectTimes,
        m.IAmAliveTime = GREATEST(m.IAmAliveTime, @IAmAliveTime)
    WHERE v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
        AND v.Version = @Version AND @Version IS NOT NULL AND v.Version < 2147483647
        AND m.Address = @Address AND @Address IS NOT NULL
        AND m.Port = @Port AND @Port IS NOT NULL
        AND m.Generation = @Generation AND @Generation IS NOT NULL;

    SELECT ROW_COUNT() > 0;
'
WHERE QueryKey = 'UpdateMembershipKey';

UPDATE OrleansQuery SET QueryText = '
    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId
        AND @DeploymentId IS NOT NULL
        AND IAmAliveTime < @IAmAliveTime
        AND StartTime < @IAmAliveTime
        AND COALESCE(SuspectTimes, '''') = ''''
        AND Status = 6;
'
WHERE QueryKey = 'CleanupDefunctSiloEntriesKey';

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT 'CleanupDefunctSiloEntryKey', '
    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId AND Status = 6
        AND Address = @Address AND Port = @Port AND Generation = @Generation
        AND IAmAliveTime = @IAmAliveTime AND StartTime = @StartTime
        AND CAST(COALESCE(SuspectTimes, '''') AS BINARY) = CAST(COALESCE(@SuspectTimes, '''') AS BINARY);
'
WHERE NOT EXISTS (SELECT 1 FROM OrleansQuery WHERE QueryKey = 'CleanupDefunctSiloEntryKey');

COMMIT;
