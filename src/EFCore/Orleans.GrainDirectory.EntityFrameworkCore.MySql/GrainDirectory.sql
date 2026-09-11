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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210051_InitialGrainDirectorySchema') THEN

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210051_InitialGrainDirectorySchema') THEN

    CREATE TABLE `Activations` (
        `ClusterIdHash` binary(32) NOT NULL,
        `GrainIdHash` binary(32) NOT NULL,
        `SiloAddressHash` binary(32) NOT NULL,
        `ClusterId` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `GrainId` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `SiloAddress` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `ActivationId` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `MembershipVersion` bigint NOT NULL,
        `ETag` char(36) COLLATE ascii_general_ci NOT NULL,
        CONSTRAINT `PK_Activations` PRIMARY KEY (`ClusterIdHash`, `GrainIdHash`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210051_InitialGrainDirectorySchema') THEN

    CREATE INDEX `IX_Activations_ClusterIdHash_SiloAddressHash` ON `Activations` (`ClusterIdHash`, `SiloAddressHash`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210051_InitialGrainDirectorySchema') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260811210051_InitialGrainDirectorySchema', '8.0.29');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;
