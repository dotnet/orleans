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
    WHERE [MigrationId] = N'20260811210132_InitialRemindersSchema'
)
BEGIN
    CREATE TABLE [Reminders] (
        [ServiceIdHash] binary(32) NOT NULL,
        [GrainIdHash] binary(32) NOT NULL,
        [ReminderNameHash] binary(32) NOT NULL,
        [ServiceId] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [GrainId] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [Name] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [StartAt] datetimeoffset NOT NULL,
        [Period] bigint NOT NULL,
        [GrainHash] bigint NOT NULL,
        [ETag] rowversion NOT NULL,
        CONSTRAINT [PK_Reminders] PRIMARY KEY NONCLUSTERED ([ServiceIdHash], [GrainIdHash], [ReminderNameHash])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811210132_InitialRemindersSchema'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IDX_Reminders_ServiceIdHash_GrainHash] ON [Reminders] ([ServiceIdHash], [GrainHash]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811210132_InitialRemindersSchema'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IDX_Reminders_ServiceIdHash_GrainIdHash] ON [Reminders] ([ServiceIdHash], [GrainIdHash]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811210132_InitialRemindersSchema'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260811210132_InitialRemindersSchema', N'8.0.29');
END;
GO

COMMIT;
GO
