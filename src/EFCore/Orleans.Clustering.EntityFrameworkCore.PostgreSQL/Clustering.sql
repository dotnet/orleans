CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;


DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811154956_InitialClusteringSchema') THEN
    CREATE TABLE "Clusters" (
        "Id" text NOT NULL,
        "Timestamp" timestamp with time zone NOT NULL,
        "Version" integer NOT NULL,
        "ETag" uuid NOT NULL,
        CONSTRAINT "PK_Clusters" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811154956_InitialClusteringSchema') THEN
    CREATE TABLE "Silos" (
        "ClusterId" text NOT NULL,
        "Address" character varying(45) NOT NULL,
        "Port" integer NOT NULL,
        "Generation" integer NOT NULL,
        "Name" character varying(150) NOT NULL,
        "HostName" character varying(150) NOT NULL,
        "Status" integer NOT NULL,
        "ProxyPort" integer,
        "SuspectingTimes" text,
        "SuspectingSilos" text,
        "StartTime" timestamp with time zone NOT NULL,
        "IAmAliveTime" timestamp with time zone NOT NULL,
        "ETag" uuid NOT NULL,
        CONSTRAINT "PK_Silos" PRIMARY KEY ("ClusterId", "Address", "Port", "Generation"),
        CONSTRAINT "FK_Silos_Clusters_ClusterId" FOREIGN KEY ("ClusterId") REFERENCES "Clusters" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811154956_InitialClusteringSchema') THEN
    CREATE INDEX "IX_Silos_ClusterId" ON "Silos" ("ClusterId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811154956_InitialClusteringSchema') THEN
    CREATE INDEX "IX_Silos_ClusterId_Status" ON "Silos" ("ClusterId", "Status");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811154956_InitialClusteringSchema') THEN
    CREATE INDEX "IX_Silos_ClusterId_Status_IAmAliveTime" ON "Silos" ("ClusterId", "Status", "IAmAliveTime");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260811154956_InitialClusteringSchema') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260811154956_InitialClusteringSchema', '8.0.29');
    END IF;
END $EF$;
COMMIT;

