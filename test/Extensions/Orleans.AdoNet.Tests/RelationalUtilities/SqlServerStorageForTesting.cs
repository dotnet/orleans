using System.Data.Common;
using Microsoft.Data.SqlClient;
using Orleans.Tests.SqlUtils;
using TestExtensions;

namespace UnitTests.General
{
    public class SqlServerStorageForTesting : RelationalStorageForTesting
    {
        protected override string ProviderMoniker => "SQLServer";

        public SqlServerStorageForTesting(string connectionString)
            : base(AdoNetInvariants.InvariantNameSqlServer, connectionString ?? TestDefaultConfiguration.MsSqlConnectionString)
        {
        }

        protected override void PrepareForDatabaseReset(string databaseName)
        {
            using var administrativeConnection = new SqlConnection(CurrentConnectionString);
            SqlConnection.ClearPool(administrativeConnection);

            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = CurrentConnectionString
            };
            builder["Database"] = databaseName;

            using var databaseConnection = new SqlConnection(builder.ConnectionString);
            SqlConnection.ClearPool(databaseConnection);
        }

        protected override async Task WaitForDatabaseReadyAsync()
        {
            const int maxAttempts = 3;
            var databaseConnectionStringBuilder = new SqlConnectionStringBuilder(CurrentConnectionString)
            {
                Pooling = false,
                ConnectTimeout = 5
            };

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var connection = new SqlConnection(databaseConnectionStringBuilder.ConnectionString);
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 1";
                    command.CommandTimeout = 5;
                    _ = await command.ExecuteScalarAsync();
                }
                catch (SqlException exception) when (exception.Number == 18456 && exception.State == 1 && attempt < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                    continue;
                }

                break;
            }

            // The first schema batch enables snapshot isolation and requires exclusive database access.
            var administrativeConnectionStringBuilder = new SqlConnectionStringBuilder(CurrentConnectionString)
            {
                InitialCatalog = "master",
                Pooling = false,
                ConnectTimeout = 5
            };

            await using var administrativeConnection = new SqlConnection(administrativeConnectionStringBuilder.ConnectionString);
            await administrativeConnection.OpenAsync();
            await using var readinessCommand = administrativeConnection.CreateCommand();
            readinessCommand.CommandText = """
                DECLARE @DatabaseId INT = DB_ID(@DatabaseName);
                WHILE EXISTS
                (
                    SELECT 1
                    FROM sys.dm_exec_sessions
                    WHERE database_id = @DatabaseId
                      AND session_id <> @@SPID
                )
                BEGIN
                    WAITFOR DELAY '00:00:00.050';
                END;
                """;
            readinessCommand.CommandTimeout = 5;
            readinessCommand.Parameters.AddWithValue("DatabaseName", databaseConnectionStringBuilder.InitialCatalog);
            await readinessCommand.ExecuteNonQueryAsync();
        }

        public override string CancellationTestQuery { get { return "WAITFOR DELAY '00:00:010'; SELECT 1; "; } }

        public override string CreateStreamTestTable { get { return "CREATE TABLE StreamingTest(Id INT NOT NULL, StreamData VARBINARY(MAX) NOT NULL);"; } }

        protected override string CreateDatabaseTemplate
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
                    MAXSIZE = 10000MB,
                    FILEGROWTH = 5MB
                )')";
            }
        }

        protected override string DropDatabaseTemplate
        {
            get
            {
                return @"USE [Master]; ALTER DATABASE [{0}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{0}];";
            }
        }

        protected override string ExistsDatabaseTemplate
        {
            get
            {
                return "SELECT CAST(COUNT(1) AS BIT) FROM sys.databases WHERE name = '{0}'";
            }
        }


        protected override IEnumerable<string> ConvertToExecutableBatches(string setupScript, string dataBaseName)
        {
            var batches = setupScript.Split(new[] {"GO"}, StringSplitOptions.RemoveEmptyEntries).ToList();

            //This removes the use of recovery log in case of database crashes, which
            //improves performance to some degree, depending on usage. For non-performance testing only.
            batches.Add(string.Format("ALTER DATABASE [{0}] SET RECOVERY SIMPLE;", dataBaseName));
            batches.Add(CreateStreamTestTable);

            return batches;
        }
    }
}
