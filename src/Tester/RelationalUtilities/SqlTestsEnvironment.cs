/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.IO;
using Orleans.SqlUtils;
using Orleans.SqlUtils.Management;


namespace UnitTests.General
{
    public static class SqlTestsEnvironment
    {
        public static IRelationalStorage Setup(string testDatabaseName)
        {
            //We need a bag of management queries for this database.
            //Currently there is only SQL Server, so this is procedure is "semi-hardcoded".
            var queryBag = CreateTestManagementQueries(AdoNetInvariants.InvariantNameSqlServer);

            Console.WriteLine("Initializing relational databases...");

            var defaultConnectionString = queryBag.GetConstant(AdoNetInvariants.InvariantNameSqlServer, RelationalTestingConstants.DefaultConnectionStringKey);
            var storage = RelationalStorage.CreateInstance(AdoNetInvariants.InvariantNameSqlServer, defaultConnectionString);
                                  
            Console.WriteLine("Dropping and recreating database '{0}' with connectionstring '{1}'", testDatabaseName, storage.ConnectionString);
            var existsQuery = queryBag.GetConstant(storage.InvariantName, RelationalTestingConstants.ExistsDatabaseKey);
            if(storage.ExistsDatabaseAsync(existsQuery, testDatabaseName).Result)
            {
                var dropQuery = queryBag.GetConstant(storage.InvariantName, RelationalTestingConstants.DropDatabaseKey);
                storage.DropDatabaseAsync(dropQuery, testDatabaseName).Wait();
            }

            var creationQuery = queryBag.GetConstant(storage.InvariantName, RelationalTestingConstants.CreateDatabaseKey);
            storage.CreateDatabaseAsync(creationQuery, testDatabaseName).Wait();

            //The old storage instance has the previous connection string, time have a new handle with a new connection string...
            storage = storage.CreateNewStorageInstance(testDatabaseName);

            Console.WriteLine("Creating database tables...");
            var creationScripts = RelationalStorageUtilities.RemoveBatchSeparators(File.ReadAllText("CreateOrleansTables_SqlServer.sql"));
            foreach (var creationScript in creationScripts)
            {
                var res = storage.ExecuteAsync(creationScript).Result;
            }

            //Currently there's only one database under test, SQL Server. So this as the other
            //setup is hardcoded here: putting the database in simple recovery mode.
            //This removes the use of recovery log in case of database crashes, which
            //improves performance to some degree, depending on usage. For non-performance testing only.
            var simpleModeRes = storage.ExecuteAsync(string.Format("ALTER DATABASE [{0}] SET RECOVERY SIMPLE;", testDatabaseName)).Result;

            storage.InitializeOrleansQueriesAsync().Wait();
            Console.WriteLine("Initializing relational databases done.");

            return storage;
        }


        public static QueryConstantsBag CreateTestManagementQueries(string invariantName)
        {            
            //This is probably the same for all the databases.
            const string DeleteAllDataTemplate =
                @"DELETE OrleansStatisticsTable;
                    DELETE OrleansClientMetricsTable;
                    DELETE OrleansSiloMetricsTable;
                    DELETE OrleansRemindersTable;
                    DELETE OrleansMembershipTable;
                    DELETE OrleansMembershipVersionTable;";
                                        
            var queryBag = new QueryConstantsBag();
            queryBag.AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameSqlServer, RelationalTestingConstants.DeleteAllDataKey, DeleteAllDataTemplate);                        
            switch(invariantName)
            {
                case(AdoNetInvariants.InvariantNameSqlServer):
                {
                    return CreateSqlServerQueries();
                }
                default:
                {
                    break;
                }
            }

            return queryBag;
        }


        private static QueryConstantsBag CreateSqlServerQueries()
        {
            var queryBag = new QueryConstantsBag();
                        
            queryBag.AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameSqlServer, RelationalTestingConstants.DefaultConnectionStringKey, @"Data Source=(localdb)\MSSQLLocalDB;Database=Master;Integrated Security=True;Asynchronous Processing=True;Max Pool Size=200; MultipleActiveResultSets=True");            
            queryBag.AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameSqlServer, RelationalTestingConstants.ExistsDatabaseKey, "SELECT CAST(COUNT(1) AS BIT) FROM sys.databases WHERE name = @databaseName");
            queryBag.AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameSqlServer, RelationalTestingConstants.CreateDatabaseKey,
                @"USE [Master];
                DECLARE @fileName AS NVARCHAR(255) = CONVERT(NVARCHAR(255), SERVERPROPERTY('instancedefaultdatapath')) + N'{0}';
                EXEC('CREATE DATABASE [{0}] ON PRIMARY 
                (
                    NAME = [{0}], 
                    FILENAME =''' + @fileName + ''', 
                    SIZE = 5MB, 
                    MAXSIZE = 100MB, 
                    FILEGROWTH = 5MB
                )')");
            queryBag.AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameSqlServer, RelationalTestingConstants.DropDatabaseKey, @"USE [Master]; ALTER DATABASE [{0}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{0}];");
            
            return queryBag;
        }
    }
}