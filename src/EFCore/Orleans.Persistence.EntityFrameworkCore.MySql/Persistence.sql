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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210104_InitialPersistenceSchema') THEN

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210104_InitialPersistenceSchema') THEN

    CREATE TABLE `GrainState` (
        `ServiceId` varchar(191) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `GrainType` varchar(191) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `StateType` varchar(191) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `GrainId` varchar(191) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `Data` longblob NULL,
        `ETag` char(36) COLLATE ascii_general_ci NOT NULL,
        CONSTRAINT `PK_GrainState` PRIMARY KEY (`ServiceId`, `GrainType`, `StateType`, `GrainId`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210104_InitialPersistenceSchema') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260811210104_InitialPersistenceSchema', '8.0.29');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;
