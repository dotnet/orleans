using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orleans.Tests.SqlUtils;
using TestExtensions;

namespace UnitTests.General
{
    internal class MySqlStorageForTesting : RelationalStorageForTesting
    {
        protected override string ProviderMoniker => "MySQL";
        public MySqlStorageForTesting(string connectionString) : base(AdoNetInvariants.InvariantNameMySql, connectionString)
        {
        }

        public override string CancellationTestQuery { get { return "DO SLEEP(10); SELECT 1;"; } }

        public override string CreateStreamTestTable { get { return "CREATE TABLE StreamingTest(Id INT NOT NULL, StreamData LONGBLOB NOT NULL);"; } }

        public IEnumerable<string> SplitScript(string setupScript)
        {
            return setupScript.Replace("END$$", "END;")
                .Split(new[] { "DELIMITER $$", "DELIMITER ;" }, StringSplitOptions.RemoveEmptyEntries);
        }

        protected override string CreateDatabaseTemplate
        {
            get { return @"CREATE DATABASE `{0}`"; }
        }

        protected override string DropDatabaseTemplate
        {
            get { return @"DROP DATABASE `{0}`"; }
        }

        public override string DefaultConnectionString => TestDefaultConfiguration.MySqlConnectionString;
         
        protected override string ExistsDatabaseTemplate
        {
            get { return "SELECT COUNT(1) FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{0}'"; }
        }

        protected override IEnumerable<string> ConvertToExecutableBatches(string setupScript, string databaseName)
        {
            var batches = setupScript.Replace("END$$", "END;").Split(new[] { "DELIMITER $$", "DELIMITER ;" }, StringSplitOptions.RemoveEmptyEntries).ToList();
            batches.Add(CreateStreamTestTable);

            return batches;
        }
    }
}
