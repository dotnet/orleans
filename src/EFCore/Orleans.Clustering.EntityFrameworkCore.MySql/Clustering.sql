CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (
    `MigrationId` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
    `ProductVersion` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    CONSTRAINT `PK___EFMigrationsHistory` PRIMARY KEY (`MigrationId`)
) CHARACTER SET=utf8mb4;

START TRANSACTION;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210037_InitialClusteringSchema') THEN

    ALTER DATABASE CHARACTER SET utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210037_InitialClusteringSchema') THEN

    CREATE TABLE `Clusters` (
        `Id` varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `Timestamp` datetime(6) NOT NULL,
        `Version` int NOT NULL,
        `ETag` char(36) COLLATE ascii_general_ci NOT NULL,
        CONSTRAINT `PK_Clusters` PRIMARY KEY (`Id`)
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210037_InitialClusteringSchema') THEN

    CREATE TABLE `Silos` (
        `ClusterId` varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `Address` varchar(45) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `Port` int NOT NULL,
        `Generation` int NOT NULL,
        `Name` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
        `HostName` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
        `Status` int NOT NULL,
        `ProxyPort` int NULL,
        `SuspectingTimes` longtext CHARACTER SET utf8mb4 NULL,
        `SuspectingSilos` longtext CHARACTER SET utf8mb4 NULL,
        `StartTime` datetime(6) NOT NULL,
        `IAmAliveTime` datetime(6) NOT NULL,
        `ETag` char(36) COLLATE ascii_general_ci NOT NULL,
        CONSTRAINT `PK_Silos` PRIMARY KEY (`ClusterId`, `Address`, `Port`, `Generation`),
        CONSTRAINT `FK_Silos_Clusters_ClusterId` FOREIGN KEY (`ClusterId`) REFERENCES `Clusters` (`Id`) ON DELETE CASCADE
    ) CHARACTER SET=utf8mb4;

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210037_InitialClusteringSchema') THEN

    CREATE INDEX `IX_Silos_ClusterId` ON `Silos` (`ClusterId`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210037_InitialClusteringSchema') THEN

    CREATE INDEX `IX_Silos_ClusterId_Status` ON `Silos` (`ClusterId`, `Status`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210037_InitialClusteringSchema') THEN

    CREATE INDEX `IX_Silos_ClusterId_Status_IAmAliveTime` ON `Silos` (`ClusterId`, `Status`, `IAmAliveTime`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210037_InitialClusteringSchema') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260811210037_InitialClusteringSchema', '8.0.29');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;
