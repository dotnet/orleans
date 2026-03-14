-- Run this migration for upgrading the PostgreSQL reminder table and routines for deployments created before 3.6.0

BEGIN;

-- Change date type

ALTER TABLE OrleansAdvancedRemindersTable
ALTER COLUMN StartTime TYPE TIMESTAMPTZ(3) USING StartTime AT TIME ZONE 'UTC';

-- Recreate routines

CREATE OR REPLACE FUNCTION upsert_reminder_row(
    ServiceIdArg    OrleansAdvancedRemindersTable.ServiceId%TYPE,
    GrainIdArg      OrleansAdvancedRemindersTable.GrainId%TYPE,
    ReminderNameArg OrleansAdvancedRemindersTable.ReminderName%TYPE,
    StartTimeArg    OrleansAdvancedRemindersTable.StartTime%TYPE,
    PeriodArg       OrleansAdvancedRemindersTable.Period%TYPE,
    GrainHashArg    OrleansAdvancedRemindersTable.GrainHash%TYPE
  )
  RETURNS TABLE(version integer) AS
$func$
DECLARE
    VersionVar int := 0;
BEGIN

    INSERT INTO OrleansAdvancedRemindersTable
    (
        ServiceId,
        GrainId,
        ReminderName,
        StartTime,
        Period,
        GrainHash,
        Version
    )
    SELECT
        ServiceIdArg,
        GrainIdArg,
        ReminderNameArg,
        StartTimeArg,
        PeriodArg,
        GrainHashArg,
        0
    ON CONFLICT (ServiceId, GrainId, ReminderName)
        DO UPDATE SET
            StartTime = excluded.StartTime,
            Period = excluded.Period,
            GrainHash = excluded.GrainHash,
            Version = OrleansAdvancedRemindersTable.Version + 1
    RETURNING
        OrleansAdvancedRemindersTable.Version INTO STRICT VersionVar;

    RETURN QUERY SELECT VersionVar AS versionr;

END
$func$ LANGUAGE plpgsql;

COMMIT;
