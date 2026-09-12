INSERT INTO OrleansQuery(QueryKey, QueryText)
SELECT
  'CleanupDefunctSiloEntriesKey','
  BEGIN
    DELETE FROM OrleansMembershipTable
      WHERE DeploymentId = :DeploymentId
        AND :DeploymentId IS NOT NULL
        AND IAmAliveTime < :IAmAliveTime
        AND Status != 3;
  END;
'
FROM dual
WHERE NOT EXISTS
(
    SELECT 1
    FROM OrleansQuery oqt
    WHERE oqt.QueryKey = 'CleanupDefunctSiloEntriesKey'
);
/
