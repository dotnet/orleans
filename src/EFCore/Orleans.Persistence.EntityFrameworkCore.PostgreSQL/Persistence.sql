CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811155003_InitialPersistenceSchema') THEN
    CREATE TABLE "GrainState" (
        "ServiceId" character varying(280) NOT NULL,
        "GrainType" character varying(280) NOT NULL,
        "StateType" character varying(280) NOT NULL,
        "GrainId" character varying(280) NOT NULL,
        "Data" bytea,
        "ETag" uuid NOT NULL,
        CONSTRAINT "PK_GrainState" PRIMARY KEY ("ServiceId", "GrainType", "StateType", "GrainId")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811155003_InitialPersistenceSchema') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260811155003_InitialPersistenceSchema', '8.0.29');
    END IF;
END $EF$;
COMMIT;
