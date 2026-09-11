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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210118_InitialRemindersSchema') THEN

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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210118_InitialRemindersSchema') THEN

    CREATE TABLE `Reminders` (
        `ServiceIdHash` binary(32) NOT NULL,
        `GrainIdHash` binary(32) NOT NULL,
        `ReminderNameHash` binary(32) NOT NULL,
        `ServiceId` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `GrainId` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `Name` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
        `StartAt` datetime(6) NOT NULL,
        `Period` bigint NOT NULL,
        `GrainHash` int unsigned NOT NULL,
        `ETag` char(36) COLLATE ascii_general_ci NOT NULL,
        CONSTRAINT `PK_Reminders` PRIMARY KEY (`ServiceIdHash`, `GrainIdHash`, `ReminderNameHash`)
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
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210118_InitialRemindersSchema') THEN

    CREATE INDEX `IX_Reminders_ServiceIdHash_GrainHash` ON `Reminders` (`ServiceIdHash`, `GrainHash`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210118_InitialRemindersSchema') THEN

    CREATE INDEX `IX_Reminders_ServiceIdHash_GrainIdHash` ON `Reminders` (`ServiceIdHash`, `GrainIdHash`);

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

DROP PROCEDURE IF EXISTS MigrationsScript;
DELIMITER //
CREATE PROCEDURE MigrationsScript()
BEGIN
    IF NOT EXISTS(SELECT 1 FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20260811210118_InitialRemindersSchema') THEN

    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20260811210118_InitialRemindersSchema', '8.0.29');

    END IF;
END //
DELIMITER ;
CALL MigrationsScript();
DROP PROCEDURE MigrationsScript;

COMMIT;
