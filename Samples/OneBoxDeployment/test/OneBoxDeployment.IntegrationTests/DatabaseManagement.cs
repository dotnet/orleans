using System;

namespace OneBoxDeployment.IntegrationTests
{
    /// <summary>
    /// Database management queries.
    /// </summary>
    public interface IManagementQueries
    {
        /// <summary>
        /// Creates a query to create a database with <paramref name="databaseName"/> to a default path.
        /// </summary>
        /// <param name="databaseName">The name of the database to create.</param>
        /// <returns>A query to create a new database with the given name to a default path.</returns>
        string CreateDatabase(string databaseName);

        /// <summary>
        /// Creates a query to create a snapshot of the <paramref name="databaseName"/> database.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <param name="databaseSnapshotName">The name of the database snapshot.</param>
        /// <returns>A query to create a new database snapshot with the given parameters to a default path.</returns>
        string CreateDatabaseSnapshot(string databaseName, string databaseSnapshotName);

        /// <summary>
        /// Creates a query to restore a database <paramref name="databaseName"/> from a <paramref name="databaseSnapshotName"/>.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <param name="databaseSnapshotName">The name of the database snapshot.</param>
        /// <returns>A query to restore a database from a snapshot with the given parameters.</returns>
        string RestoreDatabaseFromSnapshot(string databaseName, string databaseSnapshotName);

        /// <summary>
        /// Creates a query to drop a database <paramref name="databaseName"/>.
        /// </summary>
        /// <param name="databaseName">The name of the database to drop.</param>
        /// <returns>A query to drop a database with the given name.</returns>
        string DropDatabase(string databaseName);

        /// <summary>
        /// Creates a query to drop a database snapshot <paramref name="databaseSnapshotName"/>.
        /// </summary>
        /// <param name="databaseSnapshotName">The name of the database snapshot to drop.</param>
        /// <returns>A query to drop a database snapshot with the given name.</returns>
        string DropDatabaseSnapshot(string databaseSnapshotName);

        /// <summary>
        /// Creates a query to check if <paramref name="databaseName"/> exists.
        /// </summary>
        /// <param name="databaseName">The name of the database existence of which to check.</param>
        /// <returns>A query to check if a database with the given name exists.</returns>
        string ExistsDatabase(string databaseName);
    }

    /// <summary>
    /// SQL Server management queries.
    /// </summary>
    public sealed class SqlServerManagementQueries: IManagementQueries
    {
        /// <summary>
        /// A template to create a database with the given name to SQL Server instance default path.
        /// </summary>
        public string CreateDatabaseTemplate
        {
            get
            {
                return @"USE [Master];
                DECLARE @fileName AS NVARCHAR(255) = CONVERT(NVARCHAR(255), SERVERPROPERTY('instancedefaultdatapath')) + N'{0}';
                EXEC('CREATE DATABASE [{0}] ON PRIMARY
                (
                    NAME = [{0}],
                    FILENAME =''' + @fileName + ''',
                    SIZE = 20MB,
                    MAXSIZE = 100MB,
                    FILEGROWTH = 5MB
                )');";
            }
        }

        /// <summary>
        /// A template to create a snapshot of a given database with the snapshot renamed.
        /// </summary>
        public string CreateDatabaseSnapshotTemplate
        {
            get
            {
                return @"USE [Master];
                DECLARE @SourceDatabase	AS NVARCHAR(512) = N'{0}';
                DECLARE @SnapshotFileSql AS NVARCHAR(MAX) = N'';

                SELECT @SnapshotFileSql = @SnapshotFileSql +
                CASE
	                WHEN @SnapshotFileSql <> N'' THEN + N',' ELSE N''
                END +
	                N'(NAME = [' + mf.name + N'], FILENAME = ' +'N''' + LEFT(mf.physical_name, LEN(mf.physical_name) - 4) + N'_' + CONVERT(NVARCHAR(36), NEWID()) + N'.ss)'')'
                FROM
	                sys.master_files AS mf
	                INNER JOIN sys.databases AS db ON db.database_id = mf.database_id
                WHERE
	                db.state = 0
	                AND mf.type = 0
	                AND db.[name] = @SourceDatabase;

                DECLARE @SnapshotSql AS	NVARCHAR(MAX) = NULL;
                SET @SnapshotSql = N'CREATE DATABASE [{1}] ON ' + @SnapshotFileSql + N' AS SNAPSHOT OF [{0}];';
                EXEC sp_executesql @SnapshotSql;";
            }
        }

        /// <summary>
        /// A template to restore a database with a given name from a named snapshot.
        /// </summary>
        public string RestoreDatabaseFromSnapshotTemplate
        {
            get
            {
                return "USE [Master]; ALTER DATABASE [{0}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [{0}] FROM DATABASE_SNAPSHOT = N'{1}';";
            }
        }

        /// <summary>
        /// A template to drop a database with a given name. Sets the database into single user mode with immediate rollback first.
        /// </summary>
        public string DropDatabaseTemplate
        {
            get
            {
                return "USE [Master]; ALTER DATABASE [{0}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{0}];";
            }
        }


        /// <summary>
        /// A template to drop a database snapshot with a given name.
        /// </summary>
        public string DropDatabaseSnapshotTemplate
        {
            get
            {
                return "USE [Master]; DROP DATABASE [{0}];";
            }
        }

        /// <summary>
        /// A template to check the existence of a database with a given name.
        /// </summary>
        public string ExistsDatabaseTemplate
        {
            get
            {
                return "SELECT CAST(COUNT(1) AS BIT) FROM sys.databases WHERE name = N'{0}';";
            }
        }

        /// <summary>
        /// Creates a SQL Sever query to create a database with <paramref name="databaseName"/> to SQL Server instance default path.
        /// </summary>
        /// <param name="databaseName">The name of the database to create.</param>
        /// <returns>A SQL Server query to create a new database with the given name to a default path.</returns>
        public string CreateDatabase(string databaseName)
        {
            ThrowIfInvalidDatabaseNameParameter(databaseName);

            return string.Format(CreateDatabaseTemplate, databaseName);
        }

        /// <summary>
        /// Creates a SQL Server query to create a snapshot of the <paramref name="databaseName"/> database.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <param name="databaseSnapshotName">The name of the database snapshot.</param>
        /// <returns>A SQL Server query to create a new database snapshot with the given parameters to a default path.</returns>
        public string CreateDatabaseSnapshot(string databaseName, string databaseSnapshotName)
        {
            ThrowIfInvalidDatabaseNameParameter(databaseName);
            ThrowIfInvalidDatabaseSnapshotNameParameter(databaseSnapshotName);

            return string.Format(CreateDatabaseSnapshotTemplate, databaseName, databaseSnapshotName);
        }

        /// <summary>
        /// Creates a SQL Server query to restore a database <paramref name="databaseName"/> from a <paramref name="databaseSnapshotName"/>.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        /// <param name="databaseSnapshotName">The name of the database snapshot.</param>
        /// <returns>A SQL Server query to restore a database from a snapshot with the given parameters.</returns>
        public string RestoreDatabaseFromSnapshot(string databaseName, string databaseSnapshotName)
        {
            ThrowIfInvalidDatabaseNameParameter(databaseName);
            ThrowIfInvalidDatabaseSnapshotNameParameter(databaseSnapshotName);

            return string.Format(RestoreDatabaseFromSnapshotTemplate, databaseName, databaseSnapshotName);
        }

        /// <summary>
        /// Creates a SQL Server query to drop a database <paramref name="databaseName"/>.
        /// </summary>
        /// <param name="databaseName">The name of the database to drop.</param>
        /// <returns>A SQL Server query to drop a database with the given name.</returns>
        public string DropDatabase(string databaseName)
        {
            ThrowIfInvalidDatabaseNameParameter(databaseName);

            return string.Format(DropDatabaseTemplate, databaseName);
        }

        /// <summary>
        /// Creates a SQL Server query to drop a database snapshot <paramref name="databaseSnapshotName"/>.
        /// </summary>
        /// <param name="databaseSnapshotName">The name of the database snapshot to drop.</param>
        /// <returns>A SQL Server query to drop a database snapshot with the given name.</returns>
        public string DropDatabaseSnapshot(string databaseSnapshotName)
        {
            ThrowIfInvalidDatabaseSnapshotNameParameter(databaseSnapshotName);

            return string.Format(DropDatabaseSnapshotTemplate, databaseSnapshotName);
        }

        /// <summary>
        /// Creates a SQL Server query to check if <paramref name="databaseName"/> exists.
        /// </summary>
        /// <param name="databaseName">The name of the database existence of which to check.</param>
        /// <returns>A SQL Server query to check if a database with the given name exists.</returns>
        public string ExistsDatabase(string databaseName)
        {
            ThrowIfInvalidDatabaseNameParameter(databaseName);

            return string.Format(ExistsDatabaseTemplate, databaseName);
        }

        /// <summary>
        /// Checks the <paramref name="databaseName"/> parameter is valid.
        /// </summary>
        /// <param name="databaseName">The database name parameter to check.</param>
        /// <exception cref="ArgumentException" />.
        private static void ThrowIfInvalidDatabaseNameParameter(string databaseName)
        {
            if(string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException("The minimum requirement is one non-whitespace character.", nameof(databaseName));
            }
        }

        /// <summary>
        /// Checks the <paramref name="databaseSnapshotName"/> parameter is valid.
        /// </summary>
        /// <param name="databaseSnapshotName">The database name parameter to check.</param>
        /// <exception cref="ArgumentException" />.
        private static void ThrowIfInvalidDatabaseSnapshotNameParameter(string databaseSnapshotName)
        {
            if(string.IsNullOrWhiteSpace(databaseSnapshotName))
            {
                throw new ArgumentException("The minimum requirement is one non-whitespace character.", nameof(databaseSnapshotName));
            }
        }
    }
}
