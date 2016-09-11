﻿using System.Collections.Generic;
using Orleans.SqlUtils;
using UnitTests.General;

namespace Tester.RelationalUtilities
{
    class PostgreSqlStorageForTesting : RelationalStorageForTesting
    {
        public PostgreSqlStorageForTesting(string connectionString)
            : base(AdoNetInvariants.InvariantNamePostgreSql, connectionString)
        {
        }

        public override string DefaultConnectionString
        {
            get { return @"Server=127.0.0.1;Port=5432;Database=postgres;Integrated Security=true;Pooling=false;"; }
        }

        public override string CancellationTestQuery { get { return "SELECT pg_sleep(10); SELECT 1; "; } }

        public override string CreateStreamTestTable { get { return "CREATE TABLE StreamingTest(Id integer NOT NULL, StreamData bytea NOT NULL);"; } }

        protected override string SetupSqlScriptFileName
        {
            get { return "CreateOrleansTables_PostgreSql.sql"; }
        }

        protected override string CreateDatabaseTemplate
        {
            get
            {
                return @"CREATE DATABASE ""{0}"" WITH ENCODING='UTF8' CONNECTION LIMIT=-1;";
            }
        }

        protected override string DropDatabaseTemplate
        {
            get
            {
                return @"DROP DATABASE ""{0}"";";
            }
        }
        

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
            }; // setupScript.Split(new[] { "GO" }, StringSplitOptions.RemoveEmptyEntries).ToList();

            //This removes the use of recovery log in case of database crashes, which
            //improves performance to some degree, depending on usage. For non-performance testing only.

            return batches;
        }
    }
}
