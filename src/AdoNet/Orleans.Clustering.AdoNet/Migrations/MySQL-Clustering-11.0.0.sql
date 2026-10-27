-- Run this migration for upgrading the MySQL clustering procedures for deployments created before 11.0.0.
-- Stop all silos using this membership database before running the script, and restart them after it completes.
-- Procedure replacement uses separate DDL statements which commit independently. If interrupted, rerun the
-- complete script before restarting silos. Existing membership rows and versions are preserved.
-- MySQL 8.4 does not evaluate ROW_COUNT() in a WHERE clause as the count of the preceding statement, so
-- InsertMembershipKey could report a duplicate silo insert as successful and advance the membership version.

DROP PROCEDURE IF EXISTS InsertMembershipKey;

DELIMITER $$

CREATE PROCEDURE InsertMembershipKey(
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
    DECLARE _INSERTED INT;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    START TRANSACTION;

    -- Preserve the row-then-version statement order used by cached legacy inserts.
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

    -- MySQL 8.4 does not evaluate ROW_COUNT() in a WHERE clause as the count of the preceding statement.
    SET _INSERTED = ROW_COUNT();

    UPDATE OrleansMembershipVersionTable
    SET Version = Version + 1
    WHERE DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
        AND Version = _Version AND _Version IS NOT NULL
        AND _INSERTED > 0;

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
