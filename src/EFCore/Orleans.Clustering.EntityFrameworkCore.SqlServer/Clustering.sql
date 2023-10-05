IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005032242_InitialClusteringSchema')
BEGIN
    CREATE TABLE [Clusters] (
        [Id] nvarchar(450) NOT NULL,
        [Timestamp] datetimeoffset NOT NULL,
        [Version] int NOT NULL,
        [ETag] rowversion NOT NULL,
        CONSTRAINT [PK_Cluster] PRIMARY KEY NONCLUSTERED ([Id])
    );
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005032242_InitialClusteringSchema')
BEGIN
    CREATE TABLE [Silos] (
        [ClusterId] nvarchar(450) NOT NULL,
        [Address] nvarchar(45) NOT NULL,
        [Port] int NOT NULL,
        [Generation] int NOT NULL,
        [Name] nvarchar(150) NOT NULL,
        [HostName] nvarchar(150) NOT NULL,
        [Status] int NOT NULL,
        [ProxyPort] int NULL,
        [SuspectingTimes] nvarchar(max) NULL,
        [SuspectingSilos] nvarchar(max) NULL,
        [StartTime] datetimeoffset NOT NULL,
        [IAmAliveTime] datetimeoffset NOT NULL,
        [ETag] rowversion NOT NULL,
        CONSTRAINT [PK_Silo] PRIMARY KEY NONCLUSTERED ([ClusterId], [Address], [Port], [Generation]),
        CONSTRAINT [FK_Silos_Clusters_ClusterId] FOREIGN KEY ([ClusterId]) REFERENCES [Clusters] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005032242_InitialClusteringSchema')
BEGIN
    CREATE NONCLUSTERED INDEX [IDX_Silo_ClusterId] ON [Silos] ([ClusterId]);
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005032242_InitialClusteringSchema')
BEGIN
    CREATE NONCLUSTERED INDEX [IDX_Silo_ClusterId_Status] ON [Silos] ([ClusterId], [Status]);
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005032242_InitialClusteringSchema')
BEGIN
    CREATE NONCLUSTERED INDEX [IDX_Silo_ClusterId_Status_IAmAlive] ON [Silos] ([ClusterId], [Status], [IAmAliveTime]);
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005032242_InitialClusteringSchema')
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20231005032242_InitialClusteringSchema', N'7.0.11');
END;
GO

COMMIT;
GO

