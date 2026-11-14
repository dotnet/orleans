using System.Data.Common;
using Microsoft.Data.SqlClient;
using Orleans.Tests.SqlUtils;
using TestExtensions;

namespace UnitTests.General
{
    public class SqlServerStorageForTesting : RelationalStorageForTesting
    {
        private const int DeadlockVictimError = 1205;
        private const int CommandTimeoutError = -2;
        private const int DatabaseAlreadyOpenError = 924;
        private const int DatabaseLockError = 5061;
        private const int DatabaseStateChangeError = 5064;
        private const int DatabaseStateChangeFailedError = 5069;
        private const int DatabaseInUseError = 3702;
        private const int SetupCommandTimeoutSeconds = 120;
        private const string SnapshotSettingsCommand = "EXECUTE sp_executesql @snapshotSettings;";

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
            var scriptBatches = scripts.ToArray();
            if (scriptBatches.Length == 0)
            {
                return;
            }

            using var commandBuilder = new SqlCommandBuilder();
            var quotedDatabaseName = commandBuilder.QuoteIdentifier(databaseName);
            await ConfigureDatabaseAsync(quotedDatabaseName, databaseName, cancellationToken);

            var connectionStringBuilder = new SqlConnectionStringBuilder(CurrentConnectionString)
            {
                Pooling = false,
                ConnectTimeout = 5
            };

            await using var connection = new SqlConnection(connectionStringBuilder.ConnectionString);
            try
            {
                await OpenConnectionAsync(connection, cancellationToken);
                foreach (var script in scriptBatches)
                {
                    await ExecuteCommandAsync(
                        connection,
                        script,
                        cancellationToken,
                        SetupCommandTimeoutSeconds);
                }
            }
            finally
            {
                using var pooledConnection = new SqlConnection(CurrentConnectionString);
                SqlConnection.ClearPool(pooledConnection);
            }
        }

        private async Task ConfigureDatabaseAsync(
            string quotedDatabaseName,
            string databaseName,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            var connectionStringBuilder = new SqlConnectionStringBuilder(CurrentConnectionString)
            {
                InitialCatalog = "master",
                Pooling = false,
                ConnectTimeout = 5
            };

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var connection = new SqlConnection(connectionStringBuilder.ConnectionString);
                    await OpenConnectionAsync(connection, cancellationToken);
                    await ExecuteCommandAsync(
                        connection,
                        $"ALTER DATABASE {quotedDatabaseName} SET MULTI_USER WITH ROLLBACK IMMEDIATE;",
                        cancellationToken,
                        SetupCommandTimeoutSeconds);
                    await ExecuteCommandAsync(
                        connection,
                        $"ALTER DATABASE {quotedDatabaseName} SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;",
                        cancellationToken,
                        SetupCommandTimeoutSeconds);
                    await ExecuteCommandAsync(
                        connection,
                        $"ALTER DATABASE {quotedDatabaseName} SET ALLOW_SNAPSHOT_ISOLATION ON;",
                        cancellationToken,
                        SetupCommandTimeoutSeconds);
                    await ExecuteCommandAsync(
                        connection,
                        $"ALTER DATABASE {quotedDatabaseName} SET RECOVERY SIMPLE;",
                        cancellationToken,
                        SetupCommandTimeoutSeconds);
                    return;
                }
                catch (SqlException exception) when (
                    IsRetryableDatabaseSetupError(exception.Number)
                    && attempt < maxAttempts)
                {
                    Console.WriteLine(
                        "SQL Server database '{0}' configuration failed with transient error {1} on attempt {2}; retrying.",
                        databaseName,
                        exception.Number,
                        attempt);
                    using var pooledConnection = new SqlConnection(CurrentConnectionString);
                    SqlConnection.ClearPool(pooledConnection);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                }
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
            CancellationToken cancellationToken,
            int? commandTimeout = null)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = script;
            if (commandTimeout is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

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
                catch (SqlException exception) when (IsRetryableDatabaseResetError(exception.Number) && attempt < maxAttempts)
                {
                    Console.WriteLine(
                        "SQL Server database '{0}' reset failed with transient error {1} on attempt {2}; retrying.",
                        databaseName,
                        exception.Number,
                        attempt);
                    PrepareForDatabaseReset(databaseName);
                }
            }
        }

        internal static bool IsRetryableDatabaseResetError(int errorNumber) =>
            errorNumber is DeadlockVictimError
                or DatabaseAlreadyOpenError
                or DatabaseInUseError
                or DatabaseLockError
                or DatabaseStateChangeError
                or DatabaseStateChangeFailedError;

        internal static bool IsRetryableDatabaseSetupError(int errorNumber) =>
            errorNumber is CommandTimeoutError
                or DeadlockVictimError
                or DatabaseAlreadyOpenError
                or DatabaseInUseError
                or DatabaseLockError
                or DatabaseStateChangeError
                or DatabaseStateChangeFailedError;

        protected override string ExistsDatabaseTemplate
        {
            get
            {
                return "SELECT CAST(COUNT(1) AS BIT) FROM sys.databases WHERE name = '{0}'";
            }
        }


        protected override IEnumerable<string> ConvertToExecutableBatches(string setupScript, string dataBaseName)
        {
            var snapshotSettingsEnd = setupScript.IndexOf(SnapshotSettingsCommand, StringComparison.Ordinal);
            if (snapshotSettingsEnd < 0)
            {
                throw new InvalidOperationException("The SQL Server setup script does not contain the snapshot settings command.");
            }

            snapshotSettingsEnd += SnapshotSettingsCommand.Length;
            var batches = setupScript[snapshotSettingsEnd..].Split(new[] { "GO" }, StringSplitOptions.RemoveEmptyEntries).ToList();
            batches.Add(CreateStreamTestTable);

            return batches;
        }
    }
}
