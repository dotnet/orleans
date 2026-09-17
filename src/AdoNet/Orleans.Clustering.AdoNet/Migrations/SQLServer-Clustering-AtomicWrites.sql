-- Updates membership writes and adds captured-value Dead-row pruning.
-- Apply to an existing clustering database before starting the updated provider.
-- Existing table schemas, query parameters, and routine signatures are preserved.

BEGIN TRANSACTION;

UPDATE OrleansQuery SET QueryText = '-- This is expected to never fail by Orleans, so return value
	-- is not needed nor is it checked.
	SET NOCOUNT ON;
	UPDATE OrleansMembershipTable
	SET
		IAmAliveTime = CASE WHEN IAmAliveTime > @IAmAliveTime THEN IAmAliveTime ELSE @IAmAliveTime END
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL;
	'
WHERE QueryKey = 'UpdateIAmAlivetimeKey';

UPDATE OrleansQuery SET QueryText = 'SET XACT_ABORT, NOCOUNT ON;
	DECLARE @ROWCOUNT AS INT;
	BEGIN TRANSACTION;

	UPDATE OrleansMembershipVersionTable
	SET
		Timestamp = GETUTCDATE(),
		Version = Version + 1
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Version = @Version AND @Version IS NOT NULL AND Version < 2147483647;

	SET @ROWCOUNT = @@ROWCOUNT;

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
		@StartTime,
		@IAmAliveTime
	WHERE @ROWCOUNT > 0 AND NOT EXISTS
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

	SET @ROWCOUNT = @@ROWCOUNT;

	IF @ROWCOUNT = 0
		ROLLBACK TRANSACTION
	ELSE
		COMMIT TRANSACTION
	SELECT @ROWCOUNT;
	'
WHERE QueryKey = 'InsertMembershipKey';

UPDATE OrleansQuery SET QueryText = 'SET XACT_ABORT, NOCOUNT ON;
	DECLARE @ROWCOUNT AS INT;
	BEGIN TRANSACTION;

	UPDATE OrleansMembershipVersionTable
	SET
		Timestamp = GETUTCDATE(),
		Version = Version + 1
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Version = @Version AND @Version IS NOT NULL AND Version < 2147483647;

	UPDATE OrleansMembershipTable
	SET
		Status = @Status,
		SuspectTimes = @SuspectTimes,
		IAmAliveTime = CASE WHEN IAmAliveTime > @IAmAliveTime THEN IAmAliveTime ELSE @IAmAliveTime END
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL
		AND @@ROWCOUNT > 0;

	SET @ROWCOUNT = @@ROWCOUNT;
	IF @ROWCOUNT = 0
		ROLLBACK TRANSACTION;
	ELSE
		COMMIT TRANSACTION;
	SELECT @ROWCOUNT;
	'
WHERE QueryKey = 'UpdateMembershipKey';

UPDATE OrleansQuery SET QueryText = 'SELECT
		v.DeploymentId,
		m.Address,
		m.Port,
		m.Generation,
		m.SiloName,
		m.HostName,
		m.Status,
		m.ProxyPort,
		m.SuspectTimes,
		m.StartTime,
		m.IAmAliveTime,
		v.Version
	FROM
		OrleansMembershipVersionTable v WITH(HOLDLOCK)
		-- This ensures the version table will returned even if there is no matching membership row.
		LEFT OUTER JOIN OrleansMembershipTable m WITH(HOLDLOCK) ON v.DeploymentId = m.DeploymentId
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL
	WHERE
		v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
	'
WHERE QueryKey = 'MembershipReadRowKey';

UPDATE OrleansQuery SET QueryText = 'SELECT
		v.DeploymentId,
		m.Address,
		m.Port,
		m.Generation,
		m.SiloName,
		m.HostName,
		m.Status,
		m.ProxyPort,
		m.SuspectTimes,
		m.StartTime,
		m.IAmAliveTime,
		v.Version
	FROM
		OrleansMembershipVersionTable v WITH(HOLDLOCK) LEFT OUTER JOIN OrleansMembershipTable m WITH(HOLDLOCK)
		ON v.DeploymentId = m.DeploymentId
	WHERE
		v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
	'
WHERE QueryKey = 'MembershipReadAllKey';

UPDATE OrleansQuery SET QueryText = 'DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId
        AND @DeploymentId IS NOT NULL
        AND IAmAliveTime < @IAmAliveTime
        AND StartTime < @IAmAliveTime
        AND COALESCE(SuspectTimes, '''') = ''''
        AND Status = 6;
    '
WHERE QueryKey = 'CleanupDefunctSiloEntriesKey';

INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT 'CleanupDefunctSiloEntryKey', 'DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId AND Status = 6
        AND Address = @Address AND Port = @Port AND Generation = @Generation
        AND IAmAliveTime = @IAmAliveTime AND StartTime = @StartTime
        AND CONVERT(VARBINARY(8000), COALESCE(SuspectTimes, '''')) = CONVERT(VARBINARY(8000), CONVERT(VARCHAR(8000), COALESCE(@SuspectTimes, '''')));
    '
WHERE NOT EXISTS (SELECT 1 FROM OrleansQuery WHERE QueryKey = 'CleanupDefunctSiloEntryKey');

COMMIT;
