using Npgsql;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;

namespace Tester.RelationalUtilities
{
    internal class PostgreSqlStorageForTesting : RelationalStorageForTesting
    {
        protected override string ProviderMoniker => "PostgreSQL";

        public PostgreSqlStorageForTesting(string connectionString)
            : base(AdoNetInvariants.InvariantNamePostgreSql, connectionString ?? TestDefaultConfiguration.PostgresConnectionString)
        {
        }

        public override string CancellationTestQuery { get { return "SELECT pg_sleep(10); SELECT 1; "; } }

        public override string CreateStreamTestTable { get { return "CREATE TABLE StreamingTest(Id integer NOT NULL, StreamData bytea NOT NULL);"; } }


        protected override string CreateDatabaseTemplate
        {
            get
            {
                return @"CREATE DATABASE ""{0}"" WITH ENCODING='UTF8' CONNECTION LIMIT=-1;";
            }
        }

        protected override string DropDatabaseTemplate => @"DROP DATABASE ""{0}"" WITH (FORCE);";

        protected override string ExistsDatabaseTemplate
        {
            get
            {
                return "SELECT COUNT(1)::int::boolean FROM pg_database WHERE datname = '{0}';";
            }
        }


        protected override IEnumerable<string> ConvertToExecutableBatches(string setupScript, string dataBaseName)
        {

            var batches = new List<string>
            {
                setupScript,
                CreateStreamTestTable
            }; 
            

            return batches;
        }
    }
}
