CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (
    `MigrationId` varchar(150) NOT NULL,
    `ProductVersion` varchar(32) NOT NULL,
    PRIMARY KEY (`MigrationId`)
);

START TRANSACTION;

IF NOT EXISTS(SELECT * FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20231007024046_InitialClusteringSchema')
BEGIN
    CREATE TABLE `Clusters` (
        `Id` varchar(255) NOT NULL,
        `Timestamp` datetime(6) NOT NULL,
        `Version` int NOT NULL,
        `ETag` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
        PRIMARY KEY (`Id`)
    );
END;

IF NOT EXISTS(SELECT * FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20231007024046_InitialClusteringSchema')
BEGIN
    CREATE TABLE `Silos` (
        `ClusterId` varchar(255) NOT NULL,
        `Address` varchar(45) NOT NULL,
        `Port` int NOT NULL,
        `Generation` int NOT NULL,
        `Name` varchar(150) NOT NULL,
        `HostName` varchar(150) NOT NULL,
        `Status` int NOT NULL,
        `ProxyPort` int NULL,
        `SuspectingTimes` longtext NULL,
        `SuspectingSilos` longtext NULL,
        `StartTime` datetime(6) NOT NULL,
        `IAmAliveTime` datetime(6) NOT NULL,
        `ETag` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
        PRIMARY KEY (`ClusterId`, `Address`, `Port`, `Generation`),
        CONSTRAINT `FK_Silos_Clusters_ClusterId` FOREIGN KEY (`ClusterId`) REFERENCES `Clusters` (`Id`) ON DELETE CASCADE
    );
END;

IF NOT EXISTS(SELECT * FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20231007024046_InitialClusteringSchema')
BEGIN
    CREATE INDEX `IDX_Silo_ClusterId` ON `Silos` (`ClusterId`);
END;

IF NOT EXISTS(SELECT * FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20231007024046_InitialClusteringSchema')
BEGIN
    CREATE INDEX `IDX_Silo_ClusterId_Status` ON `Silos` (`ClusterId`, `Status`);
END;

IF NOT EXISTS(SELECT * FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20231007024046_InitialClusteringSchema')
BEGIN
    CREATE INDEX `IDX_Silo_ClusterId_Status_IAmAlive` ON `Silos` (`ClusterId`, `Status`, `IAmAliveTime`);
END;

IF NOT EXISTS(SELECT * FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20231007024046_InitialClusteringSchema')
BEGIN
    INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
    VALUES ('20231007024046_InitialClusteringSchema', '7.0.11');
END;

COMMIT;

