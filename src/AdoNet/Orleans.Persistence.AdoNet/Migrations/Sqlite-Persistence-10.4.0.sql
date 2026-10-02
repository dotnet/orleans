-- Run this migration for SQLite persistence databases created before Orleans 10.4.0.
-- This file is a historical migration. Add a new versioned migration instead of modifying it after release.

UPDATE OrleansQuery
SET QueryText = '
    -- TEMP objects survive pooled connection reuse. Bump the suffix on both object names whenever the trigger body changes.
    CREATE TEMP TABLE IF NOT EXISTS OrleansStorageWriteRequest_10_4
    (
        GrainIdHash INT NOT NULL,
        GrainIdN0 BIGINT NOT NULL,
        GrainIdN1 BIGINT NOT NULL,
        GrainTypeHash INT NOT NULL,
        GrainTypeString NVARCHAR(512) NOT NULL,
        GrainIdExtensionString NVARCHAR(512) NULL,
        ServiceId NVARCHAR(150) NOT NULL,
        PayloadBinary BLOB NULL,
        GrainStateVersion INT NULL,
        Applied INT NOT NULL DEFAULT 0
    );

    CREATE TEMP TRIGGER IF NOT EXISTS OrleansStorageWriteApply_10_4
    AFTER INSERT ON OrleansStorageWriteRequest_10_4
    BEGIN
        UPDATE OrleansStorage
        SET
            PayloadBinary = NEW.PayloadBinary,
            ModifiedOn = datetime(''now''),
            Version = Version + 1
        WHERE
            GrainIdHash = NEW.GrainIdHash AND GrainTypeHash = NEW.GrainTypeHash
            AND GrainIdN0 = NEW.GrainIdN0 AND GrainIdN1 = NEW.GrainIdN1
            AND GrainTypeString = NEW.GrainTypeString
            AND (GrainIdExtensionString = NEW.GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND NEW.GrainIdExtensionString IS NULL))
            AND ServiceId = NEW.ServiceId
            AND Version = NEW.GrainStateVersion;

        UPDATE OrleansStorageWriteRequest_10_4 SET Applied = changes() WHERE rowid = NEW.rowid;

        INSERT INTO OrleansStorage (GrainIdHash, GrainIdN0, GrainIdN1, GrainTypeHash, GrainTypeString, GrainIdExtensionString, ServiceId, PayloadBinary, ModifiedOn, Version)
        SELECT NEW.GrainIdHash, NEW.GrainIdN0, NEW.GrainIdN1, NEW.GrainTypeHash, NEW.GrainTypeString, NEW.GrainIdExtensionString, NEW.ServiceId, NEW.PayloadBinary, datetime(''now''), 1
        WHERE NEW.GrainStateVersion IS NULL
          AND NOT EXISTS (
            SELECT 1 FROM OrleansStorage
            WHERE GrainIdHash = NEW.GrainIdHash AND GrainTypeHash = NEW.GrainTypeHash
            AND GrainIdN0 = NEW.GrainIdN0 AND GrainIdN1 = NEW.GrainIdN1
            AND GrainTypeString = NEW.GrainTypeString
            AND (GrainIdExtensionString = NEW.GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND NEW.GrainIdExtensionString IS NULL))
            AND ServiceId = NEW.ServiceId
        );

        UPDATE OrleansStorageWriteRequest_10_4 SET Applied = Applied + changes(), PayloadBinary = NULL WHERE rowid = NEW.rowid;
    END;

    DELETE FROM OrleansStorageWriteRequest_10_4;

    INSERT INTO OrleansStorageWriteRequest_10_4 (GrainIdHash, GrainIdN0, GrainIdN1, GrainTypeHash, GrainTypeString, GrainIdExtensionString, ServiceId, PayloadBinary, GrainStateVersion)
    VALUES (@GrainIdHash, @GrainIdN0, @GrainIdN1, @GrainTypeHash, @GrainTypeString, @GrainIdExtensionString, @ServiceId, @PayloadBinary, @GrainStateVersion);

    SELECT CASE WHEN @GrainStateVersion IS NULL THEN 1 ELSE @GrainStateVersion + 1 END AS NewGrainStateVersion
    FROM OrleansStorageWriteRequest_10_4
    WHERE Applied > 0;

    SELECT @GrainStateVersion AS NewGrainStateVersion
    FROM OrleansStorageWriteRequest_10_4
    WHERE Applied = 0
        AND @GrainStateVersion IS NOT NULL;
'
WHERE QueryKey = 'WriteToStorageKey';

UPDATE OrleansQuery
SET QueryText = '
    UPDATE OrleansStorage
    SET
        PayloadBinary = NULL,
        ModifiedOn = datetime(''now''),
        Version = Version + 1
    WHERE
        GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
        AND GrainTypeString = @GrainTypeString
        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
        AND ServiceId = @ServiceId
        AND Version = @GrainStateVersion;

    SELECT @GrainStateVersion + 1 AS NewGrainStateVersion
    WHERE changes() > 0;

    SELECT @GrainStateVersion AS NewGrainStateVersion
    WHERE changes() = 0
        AND @GrainStateVersion IS NOT NULL;
'
WHERE QueryKey = 'ClearStorageKey';
