SET @metadata_column_exists = (
    SELECT COUNT(*)
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE()
      AND TABLE_NAME = 'OrleansMembershipTable'
      AND COLUMN_NAME = 'MetadataJson'
);
SET @metadata_column_statement = IF(
    @metadata_column_exists = 0,
    'ALTER TABLE OrleansMembershipTable ADD COLUMN MetadataJson LONGTEXT NULL',
    'SELECT 1'
);
PREPARE metadata_column_command FROM @metadata_column_statement;
EXECUTE metadata_column_command;
DEALLOCATE PREPARE metadata_column_command;
ALTER TABLE OrleansMembershipTable MODIFY COLUMN MetadataJson LONGTEXT NULL;

DROP PROCEDURE IF EXISTS InsertMembershipV2Key;

DELIMITER $$

CREATE PROCEDURE InsertMembershipV2Key(
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
    in    _IAmAliveTime DATETIME,
    in    _MetadataJson LONGTEXT
)
BEGIN
    DECLARE _ROWCOUNT INT;
    START TRANSACTION;
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
        MetadataJson,
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
        _MetadataJson,
        _StartTime,
        _IAmAliveTime) AS TMP
    WHERE NOT EXISTS
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

    UPDATE OrleansMembershipVersionTable
    SET
        Version = Version + 1
    WHERE
        DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
        AND Version = _Version AND _Version IS NOT NULL
        AND ROW_COUNT() > 0;

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

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'InsertMembershipV2Key','
    call InsertMembershipV2Key(@DeploymentId, @Address, @Port, @Generation,
    @Version, @SiloName, @HostName, @Status, @ProxyPort, @StartTime, @IAmAliveTime, @MetadataJson);'
)
ON DUPLICATE KEY UPDATE QueryText = VALUES(QueryText);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpdateMembershipV2Key','
    START TRANSACTION;

    UPDATE OrleansMembershipVersionTable
    SET
        Version = Version + 1
    WHERE
        DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
        AND Version = @Version AND @Version IS NOT NULL;

    UPDATE OrleansMembershipTable
    SET
        Status = @Status,
        SuspectTimes = @SuspectTimes,
        MetadataJson = @MetadataJson,
        IAmAliveTime = @IAmAliveTime
    WHERE
        DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
        AND Address = @Address AND @Address IS NOT NULL
        AND Port = @Port AND @Port IS NOT NULL
        AND Generation = @Generation AND @Generation IS NOT NULL
        AND ROW_COUNT() > 0;

    SELECT ROW_COUNT();
    COMMIT;
')
ON DUPLICATE KEY UPDATE QueryText = VALUES(QueryText);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'MembershipReadRowV2Key','
    SELECT
        v.DeploymentId,
        m.Address,
        m.Port,
        m.Generation,
        m.SiloName,
        m.HostName,
        m.Status,
        m.ProxyPort,
        m.SuspectTimes,
        m.MetadataJson,
        m.StartTime,
        m.IAmAliveTime,
        v.Version
    FROM
        OrleansMembershipVersionTable v
        LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
        AND Address = @Address AND @Address IS NOT NULL
        AND Port = @Port AND @Port IS NOT NULL
        AND Generation = @Generation AND @Generation IS NOT NULL
    WHERE
        v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
')
ON DUPLICATE KEY UPDATE QueryText = VALUES(QueryText);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'MembershipReadAllV2Key','
    SELECT
        v.DeploymentId,
        m.Address,
        m.Port,
        m.Generation,
        m.SiloName,
        m.HostName,
        m.Status,
        m.ProxyPort,
        m.SuspectTimes,
        m.MetadataJson,
        m.StartTime,
        m.IAmAliveTime,
        v.Version
    FROM
        OrleansMembershipVersionTable v LEFT OUTER JOIN OrleansMembershipTable m
        ON v.DeploymentId = m.DeploymentId
    WHERE
        v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
')
ON DUPLICATE KEY UPDATE QueryText = VALUES(QueryText);
