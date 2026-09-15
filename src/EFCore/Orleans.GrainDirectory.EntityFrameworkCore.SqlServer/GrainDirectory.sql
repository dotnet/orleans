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
    WHERE [MigrationId] = N'20260811210058_InitialGrainDirectorySchema'
)
BEGIN
    CREATE TABLE [Activations] (
        [ClusterIdHash] binary(32) NOT NULL,
        [GrainIdHash] binary(32) NOT NULL,
        [SiloAddressHash] binary(32) NOT NULL,
        [ClusterId] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [GrainId] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [SiloAddress] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [ActivationId] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [MembershipVersion] bigint NOT NULL,
        [ETag] rowversion NOT NULL,
        CONSTRAINT [PK_Activations] PRIMARY KEY NONCLUSTERED ([ClusterIdHash], [GrainIdHash])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811210058_InitialGrainDirectorySchema'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IDX_Activations_ClusterIdHash_SiloAddressHash] ON [Activations] ([ClusterIdHash], [SiloAddressHash]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260811210058_InitialGrainDirectorySchema'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260811210058_InitialGrainDirectorySchema', N'8.0.29');
END;
GO

COMMIT;
GO
