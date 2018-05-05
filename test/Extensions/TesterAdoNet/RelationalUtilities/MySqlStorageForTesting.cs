using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Tests.SqlUtils;

namespace UnitTests.General
{
    internal class MySqlStorageForTesting : RelationalStorageForTesting
    {
        public MySqlStorageForTesting(string connectionString) : base(AdoNetInvariants.InvariantNameMySql, connectionString)
        {
        }

        public override string CancellationTestQuery { get { return "DO SLEEP(10); SELECT 1;"; } }

        public override string CreateStreamTestTable { get { return "CREATE TABLE StreamingTest(Id INT NOT NULL, StreamData LONGBLOB NOT NULL);"; } }
        

        public IEnumerable<string> SplitScript(string setupScript)
        {
            return setupScript.Replace("END$$", "END;")
                .Split(new[] {"DELIMITER $$", "DELIMITER ;"}, StringSplitOptions.RemoveEmptyEntries);
        }

        protected override string CreateDatabaseTemplate
        {
            get { return @"CREATE DATABASE `{0}`"; }
        }

        protected override string DropDatabaseTemplate
        {
            get { return @"DROP DATABASE `{0}`"; }
        }

        public override string DefaultConnectionString
        {
            get { return "Server=127.0.0.1;Database=sys; Uid=root;Pwd=root;"; }
        }

        protected override string[] SetupSqlScriptFileNames
        {
            get
            {
                return new[] {
                    "MySQL-Main.sql",
                    "MySQL-Clustering.sql",
                    "MySQL-Persistence.sql",
                    "MySQL-Reminders.sql",
                    "MySQL-Statistics.sql"
                };
            }
        }

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
