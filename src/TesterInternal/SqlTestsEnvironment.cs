using System;
using System.IO;
using Orleans.Runtime.Storage.Management;
using Orleans.Runtime.Storage.Relational;
using Orleans.Runtime.Storage.RelationalExtensions;

namespace UnitTests
{
    public static class SqlTestsEnvironment
    {
        public static IRelationalStorage Setup(string testDatabaseName)
        {
            Console.WriteLine("Initializing relational databases...");
            var relationalStorage = RelationalStorageUtilities.CreateDefaultSqlServerStorageInstance();

            Console.WriteLine("Dropping and recreating database '{0}' with connectionstring '{1}'", testDatabaseName,
                relationalStorage.ConnectionString);
            if (relationalStorage.ExistsDatabaseAsync(testDatabaseName).Result)
            {
                relationalStorage.DropDatabaseAsync(testDatabaseName).Wait();
            }
            relationalStorage.CreateDatabaseAsync(testDatabaseName).Wait();

            //The old storage instance has the previous connection string, time have a new handle with a new connection string...
            relationalStorage = relationalStorage.CreateNewStorageInstance(testDatabaseName);

            Console.WriteLine("Creating database tables...");
            var creationScripts =
                RelationalStorageUtilities.RemoveBatchSeparators(File.ReadAllText("CreateOrleansTables_SqlServer.sql"));
            foreach (var creationScript in creationScripts)
            {
                var res = relationalStorage.ExecuteAsync(creationScript).Result;
            }

            //Currently there's only one database under test, SQL Server. So this as the other
            //setup is hardcoded here: putting the database in simple recovery mode.
            //This removes the use of recovery log in case of database crashes, which
            //improves performance to some degree, depending on usage. For non-performance testing only.
            var simpleModeRes =
                relationalStorage.ExecuteAsync(string.Format("ALTER DATABASE [{0}] SET RECOVERY SIMPLE;", testDatabaseName))
                                 .Result;

            relationalStorage.InitializeOrleansQueriesAsync().Wait();
            Console.WriteLine("Initializing relational databases done.");
            return relationalStorage;
        }

    }
}