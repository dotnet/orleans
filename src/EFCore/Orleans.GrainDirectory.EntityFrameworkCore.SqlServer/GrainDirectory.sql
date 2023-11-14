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

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005032142_InitialGrainDirectorySchema')
BEGIN
    CREATE TABLE [Activations] (
        [ClusterId] nvarchar(450) NOT NULL,
        [GrainId] nvarchar(450) NOT NULL,
        [SiloAddress] nvarchar(450) NOT NULL,
        [ActivationId] nvarchar(450) NOT NULL,
        [MembershipVersion] bigint NOT NULL,
        [ETag] rowversion NOT NULL,
        CONSTRAINT [PK_Activations] PRIMARY KEY NONCLUSTERED ([ClusterId], [GrainId])
    );
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005032142_InitialGrainDirectorySchema')
BEGIN
    CREATE NONCLUSTERED INDEX [IDX_Activations_ClusterId_GrainId_ActivationId] ON [Activations] ([ClusterId], [GrainId], [ActivationId]);
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005032142_InitialGrainDirectorySchema')
BEGIN
    CREATE NONCLUSTERED INDEX [IDX_Activations_CusterId_SiloAddress] ON [Activations] ([ClusterId], [SiloAddress]);
END;
GO

IF NOT EXISTS(SELECT * FROM [__EFMigrationsHistory] WHERE [MigrationId] = N'20231005032142_InitialGrainDirectorySchema')
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20231005032142_InitialGrainDirectorySchema', N'7.0.11');
END;
GO

COMMIT;
GO

