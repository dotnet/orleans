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

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811210111_InitialPersistenceSchema'
)
BEGIN
    CREATE TABLE [GrainState] (
        [ServiceId] nvarchar(280) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [GrainType] nvarchar(280) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [StateType] nvarchar(280) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [GrainId] nvarchar(280) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [Data] nvarchar(max) NULL,
        [ETag] rowversion NOT NULL,
        CONSTRAINT [PK_GrainState] PRIMARY KEY NONCLUSTERED ([ServiceId], [GrainType], [StateType], [GrainId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811210111_InitialPersistenceSchema'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260811210111_InitialPersistenceSchema', N'8.0.29');
END;
GO

COMMIT;
GO

