using System;
using System.Collections.Generic;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;

namespace Tester.RelationalUtilities
{
    class PostgreSqlStorageForTesting : RelationalStorageForTesting
    {
        protected override string ProviderMoniker => "PostgreSQL";

        public PostgreSqlStorageForTesting(string connectionString)
            : base(AdoNetInvariants.InvariantNamePostgreSql, connectionString ?? TestDefaultConfiguration.PostgresConnectionString)
        {
        }

        public override string CancellationTestQuery => "SELECT pg_sleep(10); SELECT 1; ";

        public override string CreateStreamTestTable => "CREATE TABLE StreamingTest(Id integer NOT NULL, StreamData bytea NOT NULL);";


        protected override string CreateDatabaseTemplate => @"CREATE DATABASE ""{0}"" WITH ENCODING='UTF8' CONNECTION LIMIT=-1;";

        protected override string DropDatabaseTemplate => @"SELECT pg_terminate_backend(pg_stat_activity.pid)
FROM pg_stat_activity
WHERE pg_stat_activity.datname = '{0}'
  AND pid <> pg_backend_pid();
DROP DATABASE ""{0}"";";


        protected override string ExistsDatabaseTemplate => "SELECT COUNT(1)::int::boolean FROM pg_database WHERE datname = '{0}';";


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
