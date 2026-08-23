CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811210125_InitialRemindersSchema') THEN
    CREATE TABLE "Reminders" (
        "ServiceIdHash" bytea NOT NULL,
        "GrainIdHash" bytea NOT NULL,
        "ReminderNameHash" bytea NOT NULL,
        "ServiceId" text NOT NULL,
        "GrainId" text NOT NULL,
        "Name" text NOT NULL,
        "StartAt" timestamp with time zone NOT NULL,
        "Period" bigint NOT NULL,
        "GrainHash" bigint NOT NULL,
        "ETag" uuid NOT NULL,
        CONSTRAINT "PK_Reminders" PRIMARY KEY ("ServiceIdHash", "GrainIdHash", "ReminderNameHash"),
        CONSTRAINT "CK_Reminders_GrainIdHash_Length" CHECK (octet_length("GrainIdHash") = 32),
        CONSTRAINT "CK_Reminders_ReminderNameHash_Length" CHECK (octet_length("ReminderNameHash") = 32),
        CONSTRAINT "CK_Reminders_ServiceIdHash_Length" CHECK (octet_length("ServiceIdHash") = 32)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811210125_InitialRemindersSchema') THEN
    CREATE INDEX "IX_Reminders_ServiceIdHash_GrainHash" ON "Reminders" ("ServiceIdHash", "GrainHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811210125_InitialRemindersSchema') THEN
    CREATE INDEX "IX_Reminders_ServiceIdHash_GrainIdHash" ON "Reminders" ("ServiceIdHash", "GrainIdHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811210125_InitialRemindersSchema') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260811210125_InitialRemindersSchema', '8.0.29');
    END IF;
END $EF$;
COMMIT;
