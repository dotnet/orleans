using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using Orleans.Tests.SqlUtils;
using TestExtensions;

namespace UnitTests.General
{
    public class SqlServerStorageForTesting : RelationalStorageForTesting
    {
        private const int DeadlockVictimError = 1205;
        private const int DatabaseAlreadyOpenError = 924;
        private const int DatabaseInUseError = 3702;
        private const int SetupOwnershipError = 50924;
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
            await ExecuteSetupScriptBatchesWithRetryAsync(
                scriptBatches,
                quotedDatabaseName,
                databaseName,
                cancellationToken);
        }

        private async Task ExecuteSetupScriptBatchesWithRetryAsync(
            IReadOnlyList<string> scripts,
            string quotedDatabaseName,
            string databaseName,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            var connectionStringBuilder = new SqlConnectionStringBuilder(CurrentConnectionString)
            {
                Pooling = false,
                ConnectTimeout = 5
            };

            for (var attempt = 1; ; attempt++)
            {
                var connection = new SqlConnection(connectionStringBuilder.ConnectionString);
                var setupCommandStarted = false;
                var setupSucceeded = false;
                try
                {
                    await OpenConnectionAsync(connection, cancellationToken);
                    setupCommandStarted = true;
                    await ExecuteSetupCommandAsync(
                        connection,
                        scripts,
                        quotedDatabaseName,
                        cancellationToken);
                    setupSucceeded = true;
                    return;
                }
                catch (SqlException exception) when (
                    IsRetryableDatabaseSetupError(exception.Number, setupCommandStarted)
                    && attempt < maxAttempts)
                {
                    Console.WriteLine(
                        "SQL Server database '{0}' setup failed with transient error {1} on attempt {2}; retrying.",
                        databaseName,
                        exception.Number,
                        attempt);
                }
                finally
                {
                    if (!setupSucceeded)
                    {
                        try
                        {
                            await connection.DisposeAsync();
                        }
                        catch
                        {
                            // Preserve the setup failure instead of replacing it with a disposal failure.
                        }

                        using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try
                        {
                            await RestoreMultiUserAsync(quotedDatabaseName, cleanupCancellation.Token);
                        }
                        catch
                        {
                            // Preserve the setup failure instead of replacing it with a cleanup failure.
                        }
                    }
                    else
                    {
                        await connection.DisposeAsync();
                    }

                    using var pooledConnection = new SqlConnection(CurrentConnectionString);
                    SqlConnection.ClearPool(pooledConnection);
                }
            }
        }

        private static async Task ExecuteSetupCommandAsync(
            SqlConnection connection,
            IReadOnlyList<string> scripts,
            string quotedDatabaseName,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            var commandText = new StringBuilder(
                $"""
                DECLARE @FirstBatchCompleted bit = 0;
                BEGIN TRY
                    ALTER DATABASE {quotedDatabaseName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;

                """);

            for (var i = 0; i < scripts.Count; i++)
            {
                var parameterName = $"@Batch{i}";
                commandText.Append("    EXEC sys.sp_executesql ").Append(parameterName).AppendLine(";");
                command.Parameters.Add(parameterName, SqlDbType.NVarChar, -1).Value = scripts[i];
                if (i == 0)
                {
                    commandText.AppendLine("    SET @FirstBatchCompleted = 1;");
                }
            }

            commandText.Append(
                $"""
                    ALTER DATABASE {quotedDatabaseName} SET MULTI_USER WITH ROLLBACK IMMEDIATE;
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER() = {DatabaseAlreadyOpenError} AND @FirstBatchCompleted = 0
                    BEGIN
                        ;THROW {SetupOwnershipError}, N'Failed to acquire SQL Server setup ownership.', 1;
                    END;

                    ;THROW;
                END CATCH;
                """);

            command.CommandText = commandText.ToString();
            command.CommandTimeout = SetupCommandTimeoutSeconds;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task RestoreMultiUserAsync(
            string quotedDatabaseName,
            CancellationToken cancellationToken)
        {
            var connectionStringBuilder = new SqlConnectionStringBuilder(CurrentConnectionString)
            {
                InitialCatalog = "master",
                Pooling = false,
                ConnectTimeout = 5
            };

            await using var connection = new SqlConnection(connectionStringBuilder.ConnectionString);
            await OpenConnectionAsync(connection, cancellationToken);
            await ExecuteCommandAsync(
                connection,
                $"ALTER DATABASE {quotedDatabaseName} SET MULTI_USER WITH ROLLBACK IMMEDIATE;",
                cancellationToken);
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
            errorNumber is DeadlockVictimError or DatabaseInUseError;

        internal static bool IsRetryableDatabaseSetupError(int errorNumber, bool setupCommandStarted) =>
            setupCommandStarted
                ? errorNumber is SetupOwnershipError
                : errorNumber is DatabaseAlreadyOpenError;

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
            var batches = new List<string>
            {
                setupScript[..snapshotSettingsEnd]
            };
            batches.AddRange(setupScript[snapshotSettingsEnd..].Split(new[] { "GO" }, StringSplitOptions.RemoveEmptyEntries));

            //This removes the use of recovery log in case of database crashes, which
            //improves performance to some degree, depending on usage. For non-performance testing only.
            batches.Add(string.Format("ALTER DATABASE [{0}] SET RECOVERY SIMPLE;", dataBaseName));
            batches.Add(CreateStreamTestTable);

            return batches;
        }
    }
}
