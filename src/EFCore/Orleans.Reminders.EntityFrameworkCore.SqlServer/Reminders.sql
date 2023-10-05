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

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005034632_InitialRemindersSchema')
BEGIN
    CREATE TABLE [Reminders] (
        [ServiceId] nvarchar(450) NOT NULL,
        [GrainId] nvarchar(450) NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        [StartAt] datetimeoffset NOT NULL,
        [Period] time NOT NULL,
        [GrainHash] bigint NOT NULL,
        [ETag] rowversion NOT NULL,
        CONSTRAINT [PK_Reminders] PRIMARY KEY ([ServiceId], [GrainId], [Name])
    );
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005034632_InitialRemindersSchema')
BEGIN
    CREATE NONCLUSTERED INDEX [IDX_Reminders_ServiceId_GrainHash] ON [Reminders] ([ServiceId], [GrainHash]);
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005034632_InitialRemindersSchema')
BEGIN
    CREATE NONCLUSTERED INDEX [IDX_Reminders_ServiceId_GrainId] ON [Reminders] ([ServiceId], [GrainId]);
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005034632_InitialRemindersSchema')
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20231005034632_InitialRemindersSchema', N'7.0.11');
END;
GO

COMMIT;
GO

