/*
  Queries the system of queries and their elapsed times.
*/
CREATE VIEW [Core].[SystemQueryElapsedTimes] AS
SELECT DISTINCT
	t.text AS QueryName,
	--qp.query_plan AS QueryPlan,
	s.execution_count AS 'ExecutionCount',
	COALESCE(s.execution_count / NULLIF(DATEDIFF(s, s.creation_time, CURRENT_TIMESTAMP), 0), 0) AS 'FrequencyPerSeconds',
	total_logical_reads / s.execution_count AS 'AvgLogicalReads',
    total_logical_writes / s.execution_count AS 'AvgLogicalWrites',
    total_physical_reads / s.execution_count AS 'AvgPhysicalReads',
	s.total_worker_time AS 'CPUPerMicroSecond',
	s.total_worker_time / 1000000 AS 'CPUPerSecond',
	(s.total_worker_time / 1000000) / s.execution_count AS 'AvgCPUPerSecond',
	s.max_elapsed_time AS 'MaxElapsedTimePerMicroSecond',
	s.max_elapsed_time / 1000000 AS 'MaxElapsedTimePerSecond',
	s.total_elapsed_time / 1000000 AS 'TotalElapsedTimePerSecond',
	COALESCE(s.total_elapsed_time / NULLIF(s.execution_count, 0), 0) / 1000000 AS 'AvgTotalElapsedTimePerSecond',
	s.creation_time AS 'LogCreatedOn'
FROM
	[sys].[dm_exec_query_stats] s
	CROSS APPLY [sys].[dm_exec_sql_text](s.sql_handle) t;
	--CROSS apply sys.dm_exec_query_plan (s.plan_handle) AS qp