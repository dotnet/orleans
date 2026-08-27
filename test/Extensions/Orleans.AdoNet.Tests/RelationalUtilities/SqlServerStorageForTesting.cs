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

        protected override async Task WaitForDatabaseReadyAsync(CancellationToken cancellationToken)
        {
            var databaseConnectionStringBuilder = new SqlConnectionStringBuilder(CurrentConnectionString)
            {
                Pooling = false,
                ConnectTimeout = 5
            };

            await using var connection = new SqlConnection(databaseConnectionStringBuilder.ConnectionString);
            await OpenConnectionAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;
            _ = await command.ExecuteScalarAsync(cancellationToken);
        }

        protected override Task ExecuteSetupScript(
            string setupScript,
            string dataBaseName,
            CancellationToken cancellationToken) =>
            ExecuteSetupScriptBatchesAsync(
                ConvertToExecutableBatches(setupScript, dataBaseName),
                dataBaseName,
                cancellationToken);

        internal async Task ExecuteSetupScriptBatchesAsync(
            IEnumerable<string> scripts,
            string databaseName,
            CancellationToken cancellationToken)
        {
            var connectionStringBuilder = new SqlConnectionStringBuilder(CurrentConnectionString)
            {
                Pooling = false,
                ConnectTimeout = 5
            };

            await using var connection = new SqlConnection(connectionStringBuilder.ConnectionString);
            await OpenConnectionAsync(connection, cancellationToken);

            using var commandBuilder = new SqlCommandBuilder();
            var quotedDatabaseName = commandBuilder.QuoteIdentifier(databaseName);
            using var scriptEnumerator = scripts.GetEnumerator();
            if (!scriptEnumerator.MoveNext())
            {
                return;
            }

            var setupSucceeded = false;
            try
            {
                await ExecuteCommandAsync(
                    connection,
                    $"ALTER DATABASE {quotedDatabaseName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;\n{scriptEnumerator.Current}",
                    cancellationToken);

                while (scriptEnumerator.MoveNext())
                {
                    await ExecuteCommandAsync(connection, scriptEnumerator.Current, cancellationToken);
                }

                setupSucceeded = true;
            }
            finally
            {
                using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await ExecuteCommandAsync(
                        connection,
                        $"ALTER DATABASE {quotedDatabaseName} SET MULTI_USER;",
                        cleanupCancellation.Token);
                }
                catch when (!setupSucceeded)
                {
                    // Preserve the setup failure instead of replacing it with a cleanup failure.
                }

                using var pooledConnection = new SqlConnection(CurrentConnectionString);
                SqlConnection.ClearPool(pooledConnection);
            }
        }

        private static async Task OpenConnectionAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            const int maxAttempts = 10;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await connection.OpenAsync(cancellationToken);
                    return;
                }
                catch (SqlException exception) when (exception.Number == 18456 && exception.State == 1 && attempt < maxAttempts)
                {
                    SqlConnection.ClearPool(connection);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
        }

        private static async Task ExecuteCommandAsync(
            SqlConnection connection,
            string script,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(cancellationToken);
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

        protected override async Task DropDatabaseAsync(string databaseName, CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await base.DropDatabaseAsync(databaseName, cancellationToken);
                    return;
                }
                catch (SqlException exception) when (exception.Number == 3702 && attempt < maxAttempts)
                {
                    Console.WriteLine("SQL Server database '{0}' remained in use after reset attempt {1}; retrying.", databaseName, attempt);
                    PrepareForDatabaseReset(databaseName);
                }
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
