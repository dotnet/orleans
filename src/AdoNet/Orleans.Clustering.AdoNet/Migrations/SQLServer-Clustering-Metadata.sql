IF COL_LENGTH(N'OrleansMembershipTable', N'MetadataJson') IS NULL
BEGIN
    ALTER TABLE OrleansMembershipTable ADD MetadataJson NVARCHAR(MAX) NULL;
END;
ELSE IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'OrleansMembershipTable')
      AND name = N'MetadataJson'
      AND (max_length <> -1 OR is_nullable = 0)
)
BEGIN
    ALTER TABLE OrleansMembershipTable ALTER COLUMN MetadataJson NVARCHAR(MAX) NULL;
END;

BEGIN
    DECLARE @InsertQueryText NVARCHAR(MAX) = 'SET XACT_ABORT, NOCOUNT ON;
    DECLARE @ROWCOUNT AS INT;
    BEGIN TRANSACTION;
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
        @DeploymentId,
        @Address,
        @Port,
        @Generation,
        @SiloName,
        @HostName,
        @Status,
        @ProxyPort,
        @MetadataJson,
        @StartTime,
        @IAmAliveTime
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM
            OrleansMembershipTable WITH(HOLDLOCK, XLOCK, ROWLOCK)
        WHERE
            DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
            AND Address = @Address AND @Address IS NOT NULL
            AND Port = @Port AND @Port IS NOT NULL
            AND Generation = @Generation AND @Generation IS NOT NULL
    );

    UPDATE OrleansMembershipVersionTable
    SET
        Timestamp = GETUTCDATE(),
        Version = Version + 1
    WHERE
        DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
        AND Version = @Version AND @Version IS NOT NULL
        AND @@ROWCOUNT > 0;

    SET @ROWCOUNT = @@ROWCOUNT;

    IF @ROWCOUNT = 0
        ROLLBACK TRANSACTION
    ELSE
        COMMIT TRANSACTION
    SELECT @ROWCOUNT;
    ';

    UPDATE OrleansQuery SET QueryText = @InsertQueryText WHERE QueryKey = 'InsertMembershipV2Key';
    IF @@ROWCOUNT = 0
        INSERT INTO OrleansQuery(QueryKey, QueryText) VALUES ('InsertMembershipV2Key', @InsertQueryText);
END;

BEGIN
    DECLARE @UpdateQueryText NVARCHAR(MAX) = 'SET XACT_ABORT, NOCOUNT ON;
    BEGIN TRANSACTION;

    UPDATE OrleansMembershipVersionTable
    SET
        Timestamp = GETUTCDATE(),
        Version = Version + 1
    WHERE
        DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
        AND Version = @Version AND @Version IS NOT NULL;

    UPDATE OrleansMembershipTable
    SET
        Status = @Status,
        SuspectTimes = @SuspectTimes,
        MetadataJson = @MetadataJson,
        IAmAliveTime = @IAmAliveTime
    WHERE
        DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
        AND Address = @Address AND @Address IS NOT NULL
        AND Port = @Port AND @Port IS NOT NULL
        AND Generation = @Generation AND @Generation IS NOT NULL
        AND @@ROWCOUNT > 0;

    SELECT @@ROWCOUNT;
    COMMIT TRANSACTION;
    ';

    UPDATE OrleansQuery SET QueryText = @UpdateQueryText WHERE QueryKey = 'UpdateMembershipV2Key';
    IF @@ROWCOUNT = 0
        INSERT INTO OrleansQuery(QueryKey, QueryText) VALUES ('UpdateMembershipV2Key', @UpdateQueryText);
END;

BEGIN
    DECLARE @ReadRowQueryText NVARCHAR(MAX) = 'SELECT
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
    ';

    UPDATE OrleansQuery SET QueryText = @ReadRowQueryText WHERE QueryKey = 'MembershipReadRowV2Key';
    IF @@ROWCOUNT = 0
        INSERT INTO OrleansQuery(QueryKey, QueryText) VALUES ('MembershipReadRowV2Key', @ReadRowQueryText);
END;

BEGIN
    DECLARE @ReadAllQueryText NVARCHAR(MAX) = 'SELECT
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
    ';

    UPDATE OrleansQuery SET QueryText = @ReadAllQueryText WHERE QueryKey = 'MembershipReadAllV2Key';
    IF @@ROWCOUNT = 0
        INSERT INTO OrleansQuery(QueryKey, QueryText) VALUES ('MembershipReadAllV2Key', @ReadAllQueryText);
END;
