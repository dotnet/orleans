using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime.Configuration;

namespace UnitTests.TestHelper
{
    public static class TestUtils
    {
        private static FileInfo GetDbFileLocation(DirectoryInfo dbDir, string dbFileName)
        {
            if (!dbDir.Exists) throw new FileNotFoundException("DB directory " + dbDir.FullName + " does not exist.");

            string dbDirPath = dbDir.FullName;
            FileInfo dbFile = new FileInfo(Path.Combine(dbDirPath, "TestDb.mdf"));
            Console.WriteLine("DB file location = {0}", dbFile.FullName);

            // Make sure we can write to local copy of the DB file.
            MakeDbFileWriteable(dbFile);

            return dbFile;
        }

        public static string GetSqlConnectionString(TestContext context)
        {
            string dbFileName = @"TestDb.mdf";
            string dbDirPath =
#if DEBUG
                // TestRunDirectory=C:\Depot\Orleans\Code\Main\OrleansV4\TestResults\Deploy_jthelin 2014-08-17 11_53_46
                Path.Combine(context.TestRunDirectory, @"..\..\TesterInternal\Data");
#else
                context.DeploymentDirectory;
#endif
            return GetSqlConnectionString(new DirectoryInfo(dbDirPath), dbFileName);
        }

        private static string GetSqlConnectionString(DirectoryInfo dbDir, string dbFileName)
        {
            FileInfo dbFile = GetDbFileLocation(dbDir, dbFileName);

            Console.WriteLine("DB directory = {0}", dbDir.FullName);
            Console.WriteLine("DB file = {0}", dbFile.FullName);

            string connectionString = string.Format(
                @"Data Source=(LocalDB)\v11.0;"
                + @"AttachDbFilename={0};"
                + @"Integrated Security=True;"
                + @"Connect Timeout=30",
                dbFile.FullName);

            Console.WriteLine("SQL Connection String = {0}", ConfigUtilities.RedactConnectionStringInfo(connectionString));
            return connectionString;
        }

        private static void MakeDbFileWriteable(FileInfo dbFile)
        {
            // Make sure we can write to the directory containing the DB file.
            FileInfo dbDirFile = new FileInfo(dbFile.Directory.FullName);
            if (dbDirFile.IsReadOnly)
            {
                Console.WriteLine("Making writeable directory containing DB file {0}", dbDirFile.FullName);
                dbDirFile.IsReadOnly = false;
            }
            else
            {
                Console.WriteLine("Directory containing DB file is writeable {0}", dbDirFile.FullName);
            }

            // Make sure we can write to local copy of the DB file.
            if (dbFile.IsReadOnly)
            {
                Console.WriteLine("Making writeable DB file {0}", dbFile.FullName);
                dbFile.IsReadOnly = false;
            }
            else
            {
                Console.WriteLine("DB file is writeable {0}", dbFile.FullName);
            }
        }
    }
}
