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