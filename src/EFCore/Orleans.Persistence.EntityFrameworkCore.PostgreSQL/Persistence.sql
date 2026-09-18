CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260823101503_InitialPersistenceSchema') THEN
    CREATE TABLE "GrainState" (
        "KeyHash" bytea NOT NULL,
        "ServiceId" text NOT NULL,
        "GrainType" text NOT NULL,
        "StateType" text NOT NULL,
        "GrainId" text NOT NULL,
        "Data" bytea,
        "ETag" uuid NOT NULL,
        CONSTRAINT "PK_GrainState" PRIMARY KEY ("KeyHash")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260823101503_InitialPersistenceSchema') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260823101503_InitialPersistenceSchema', '8.0.29');
    END IF;
END $EF$;
COMMIT;
