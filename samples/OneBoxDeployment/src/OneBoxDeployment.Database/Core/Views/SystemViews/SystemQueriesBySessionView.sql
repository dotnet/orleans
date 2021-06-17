/*
  Show database queries grouped by session.
*/
CREATE VIEW [Core].[SystemQueriesBySessionView] AS
SELECT
	connection.session_id AS Session,
	sson.host_name AS HostName,
	sson.login_name AS LoginName,
	sson.login_time AS LogOn,
	sson.status AS [Status],
	sql.text AS [Query]
FROM [sys].[dm_exec_connections] connection
	INNER JOIN [sys].[dm_exec_sessions] sson ON connection.session_id = sson.session_id
    CROSS APPLY [sys].[dm_exec_sql_text](most_recent_sql_handle) AS [sql];