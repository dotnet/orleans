using System;
using System.Collections.Generic;
using System.Linq;
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
                    MAXSIZE = 100MB,
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
