ALTER TABLE OrleansMembershipTable ADD COLUMN IF NOT EXISTS MetadataJson text NULL;

CREATE OR REPLACE FUNCTION insert_membership(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE,
    AddressArg      OrleansMembershipTable.Address%TYPE,
    PortArg         OrleansMembershipTable.Port%TYPE,
    GenerationArg   OrleansMembershipTable.Generation%TYPE,
    SiloNameArg     OrleansMembershipTable.SiloName%TYPE,
    HostNameArg     OrleansMembershipTable.HostName%TYPE,
    StatusArg       OrleansMembershipTable.Status%TYPE,
    ProxyPortArg    OrleansMembershipTable.ProxyPort%TYPE,
    StartTimeArg    OrleansMembershipTable.StartTime%TYPE,
    IAmAliveTimeArg OrleansMembershipTable.IAmAliveTime%TYPE,
    MetadataJsonArg OrleansMembershipTable.MetadataJson%TYPE,
    VersionArg      OrleansMembershipVersionTable.Version%TYPE)
  RETURNS TABLE(row_count integer) AS
$func$
DECLARE
    RowCountVar int := 0;
BEGIN
    BEGIN
        INSERT INTO OrleansMembershipTable
        (
            DeploymentId,
            Address,
            Port,
            Generation,
            SiloName,
            HostName,
            Status,
            ProxyPort,
            MetadataJson,
            StartTime,
            IAmAliveTime
        )
        SELECT
            DeploymentIdArg,
            AddressArg,
            PortArg,
            GenerationArg,
            SiloNameArg,
            HostNameArg,
            StatusArg,
            ProxyPortArg,
            MetadataJsonArg,
            StartTimeArg,
            IAmAliveTimeArg
        ON CONFLICT (DeploymentId, Address, Port, Generation) DO NOTHING;

        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        UPDATE OrleansMembershipVersionTable
        SET
            Timestamp = now(),
            Version = Version + 1
        WHERE
            DeploymentId = DeploymentIdArg AND DeploymentIdArg IS NOT NULL
            AND Version = VersionArg AND VersionArg IS NOT NULL
            AND RowCountVar > 0;

        GET DIAGNOSTICS RowCountVar = ROW_COUNT;
        ASSERT RowCountVar <> 0, 'no rows affected, rollback';
        RETURN QUERY SELECT RowCountVar;
    EXCEPTION
    WHEN assert_failure THEN
        RETURN QUERY SELECT RowCountVar;
    END;
END
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION update_membership(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE,
    AddressArg      OrleansMembershipTable.Address%TYPE,
    PortArg         OrleansMembershipTable.Port%TYPE,
    GenerationArg   OrleansMembershipTable.Generation%TYPE,
    StatusArg       OrleansMembershipTable.Status%TYPE,
    SuspectTimesArg OrleansMembershipTable.SuspectTimes%TYPE,
    IAmAliveTimeArg OrleansMembershipTable.IAmAliveTime%TYPE,
    MetadataJsonArg OrleansMembershipTable.MetadataJson%TYPE,
    VersionArg      OrleansMembershipVersionTable.Version%TYPE
  )
  RETURNS TABLE(row_count integer) AS
$func$
DECLARE
    RowCountVar int := 0;
BEGIN
    BEGIN
        UPDATE OrleansMembershipVersionTable
        SET
            Timestamp = now(),
            Version = Version + 1
        WHERE
            DeploymentId = DeploymentIdArg AND DeploymentIdArg IS NOT NULL
            AND Version = VersionArg AND VersionArg IS NOT NULL;

        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        UPDATE OrleansMembershipTable
        SET
            Status = StatusArg,
            SuspectTimes = SuspectTimesArg,
            MetadataJson = MetadataJsonArg,
            IAmAliveTime = IAmAliveTimeArg
        WHERE
            DeploymentId = DeploymentIdArg AND DeploymentIdArg IS NOT NULL
            AND Address = AddressArg AND AddressArg IS NOT NULL
            AND Port = PortArg AND PortArg IS NOT NULL
            AND Generation = GenerationArg AND GenerationArg IS NOT NULL
            AND RowCountVar > 0;

        GET DIAGNOSTICS RowCountVar = ROW_COUNT;
        ASSERT RowCountVar <> 0, 'no rows affected, rollback';
        RETURN QUERY SELECT RowCountVar;
    EXCEPTION
    WHEN assert_failure THEN
        RETURN QUERY SELECT RowCountVar;
    END;
END
$func$ LANGUAGE plpgsql;

UPDATE OrleansQuery
SET QueryText = $query$
    SELECT * FROM insert_membership(
        @DeploymentId,
        @Address,
        @Port,
        @Generation,
        @SiloName,
        @HostName,
        @Status,
        @ProxyPort,
        @StartTime,
        @IAmAliveTime,
        @MetadataJson,
        @Version
    );
$query$
WHERE QueryKey = 'InsertMembershipKey';

UPDATE OrleansQuery
SET QueryText = $query$
    SELECT * FROM update_membership(
        @DeploymentId,
        @Address,
        @Port,
        @Generation,
        @Status,
        @SuspectTimes,
        @IAmAliveTime,
        @MetadataJson,
        @Version
    );
$query$
WHERE QueryKey = 'UpdateMembershipKey';

UPDATE OrleansQuery
SET QueryText = $query$
    SELECT
        v.DeploymentId,
        m.Address,
        m.Port,
        m.Generation,
        m.SiloName,
        m.HostName,
        m.Status,
        m.ProxyPort,
        m.SuspectTimes,
        m.MetadataJson,
        m.StartTime,
        m.IAmAliveTime,
        v.Version
    FROM
        OrleansMembershipVersionTable v
        LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
        AND Address = @Address AND @Address IS NOT NULL
        AND Port = @Port AND @Port IS NOT NULL
        AND Generation = @Generation AND @Generation IS NOT NULL
    WHERE
        v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
$query$
WHERE QueryKey = 'MembershipReadRowKey';

UPDATE OrleansQuery
SET QueryText = $query$
    SELECT
        v.DeploymentId,
        m.Address,
        m.Port,
        m.Generation,
        m.SiloName,
        m.HostName,
        m.Status,
        m.ProxyPort,
        m.SuspectTimes,
        m.MetadataJson,
        m.StartTime,
        m.IAmAliveTime,
        v.Version
    FROM
        OrleansMembershipVersionTable v LEFT OUTER JOIN OrleansMembershipTable m
        ON v.DeploymentId = m.DeploymentId
    WHERE
        v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
$query$
WHERE QueryKey = 'MembershipReadAllKey';
