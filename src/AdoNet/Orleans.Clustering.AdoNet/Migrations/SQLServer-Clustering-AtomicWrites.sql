-- Optional enhancements for an existing clustering database.
-- Existing provider queries remain supported without applying this script.
-- Existing table schemas, query parameters, and routine signatures are preserved.

BEGIN TRANSACTION;

UPDATE OrleansQuery SET QueryText = '-- This is expected to never fail by Orleans, so return value
	-- is not needed nor is it checked.
	SET NOCOUNT ON;
	UPDATE OrleansMembershipTable
	SET
		IAmAliveTime = @IAmAliveTime
	WHERE
		DeploymentId = @DeploymentId
		AND Address = @Address
		AND Port = @Port
		AND Generation = @Generation;
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
		AND Version = @Version AND @Version IS NOT NULL;

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
