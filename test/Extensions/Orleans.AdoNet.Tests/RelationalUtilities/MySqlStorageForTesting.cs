using MySql.Data.MySqlClient;
using Orleans.Tests.SqlUtils;
using TestExtensions;

namespace UnitTests.General
{
    internal class MySqlStorageForTesting : RelationalStorageForTesting
    {
        protected override string ProviderMoniker => "MySQL";
        public MySqlStorageForTesting(string connectionString) : base(AdoNetInvariants.InvariantNameMySql, connectionString ?? TestDefaultConfiguration.MySqlConnectionString)
        {
        }

        public override string CancellationTestQuery { get { return "DO SLEEP(10); SELECT 1;"; } }

        public override string CreateStreamTestTable { get { return "CREATE TABLE StreamingTest(Id INT NOT NULL, StreamData LONGBLOB NOT NULL);"; } }

        /// <summary>
        /// Executes a script with the MySQL client's DELIMITER semantics, as users run migrations.
        /// </summary>
        /// <remarks>
        /// <see cref="SplitScript"/> rewrites procedure terminators and splits on DELIMITER lines, so it accepts scripts
        /// which the MySQL client rejects. Run migrations through this method to validate them as shipped.
        /// </remarks>
        public static async Task ExecuteClientScriptAsync(string connectionString, string script, CancellationToken cancellationToken)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await new MySqlScript(connection, script).ExecuteAsync(cancellationToken);
        }

        public IEnumerable<string> SplitScript(string setupScript)
        {
            return setupScript.Replace("END$$", "END;")
                .Split(new[] { "DELIMITER $$", "DELIMITER ;" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        protected override string CreateDatabaseTemplate
        {
            get { return @"CREATE DATABASE `{0}`"; }
        }

        protected override string DropDatabaseTemplate
        {
            get { return @"DROP DATABASE `{0}`"; }
        }

        protected override string ExistsDatabaseTemplate
        {
            get { return "SELECT COUNT(1) FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{0}'"; }
        }

        protected override IEnumerable<string> ConvertToExecutableBatches(string setupScript, string databaseName)
        {
            var batches = SplitScript(setupScript).ToList();
            batches.Add(CreateStreamTestTable);

            return batches;
        }
    }
}
