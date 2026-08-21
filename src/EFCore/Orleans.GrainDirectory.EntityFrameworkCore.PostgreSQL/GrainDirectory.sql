CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811155000_InitialGrainDirectorySchema') THEN
    CREATE TABLE "Activations" (
        "ClusterId" text NOT NULL,
        "GrainId" text NOT NULL,
        "SiloAddress" text NOT NULL,
        "ActivationId" text NOT NULL,
        "MembershipVersion" bigint NOT NULL,
        "ETag" uuid NOT NULL,
        CONSTRAINT "PK_Activations" PRIMARY KEY ("ClusterId", "GrainId")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811155000_InitialGrainDirectorySchema') THEN
    CREATE INDEX "IX_Activations_ClusterId_GrainId_ActivationId" ON "Activations" ("ClusterId", "GrainId", "ActivationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811155000_InitialGrainDirectorySchema') THEN
    CREATE INDEX "IX_Activations_ClusterId_SiloAddress" ON "Activations" ("ClusterId", "SiloAddress");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811155000_InitialGrainDirectorySchema') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260811155000_InitialGrainDirectorySchema', '8.0.29');
    END IF;
END $EF$;
COMMIT;
