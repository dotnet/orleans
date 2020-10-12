using System.IO;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace UnitTests.TestHelper
{
    public static class TestUtils
    {
        private static FileInfo GetDbFileLocation(DirectoryInfo dbDir, string dbFileName)
        {
            if (!dbDir.Exists) throw new FileNotFoundException("DB directory " + dbDir.FullName + " does not exist.");

            string dbDirPath = dbDir.FullName;
            FileInfo dbFile = new FileInfo(Path.Combine(dbDirPath, "TestDb.mdf"));
            //Console.WriteLine("DB file location = {0}", dbFile.FullName);

            // Make sure we can write to local copy of the DB file.
            MakeDbFileWriteable(dbFile);

            return dbFile;
        }

        public static string GetAdoNetConnectionString()
        {
            string dbFileName = @"TestDb.mdf";
            return GetAdoNetConnectionString(new DirectoryInfo(@".\Data"), dbFileName);
        }

        private static string GetAdoNetConnectionString(DirectoryInfo dbDir, string dbFileName)
        {
            FileInfo dbFile = GetDbFileLocation(dbDir, dbFileName);

            //Console.WriteLine("DB directory = {0}", dbDir.FullName);
            //Console.WriteLine("DB file = {0}", dbFile.FullName);

            string connectionString = string.Format(
                @"Data Source=(localdb)\mssqllocaldb;"
                + @"AttachDbFilename={0};"
                + @"Integrated Security=True;"
                + @"Connect Timeout=30",
                dbFile.FullName);

            //Console.WriteLine("SQL Connection String = {0}", ConfigUtilities.RedactConnectionStringInfo(connectionString));
            return connectionString;
        }

        private static void MakeDbFileWriteable(FileInfo dbFile)
        {
            // Make sure we can write to the directory containing the DB file.
            FileInfo dbDirFile = new FileInfo(dbFile.Directory.FullName);
            if (dbDirFile.IsReadOnly)
            {
                //Console.WriteLine("Making writeable directory containing DB file {0}", dbDirFile.FullName);
                dbDirFile.IsReadOnly = false;
            }
            else
            {
                //Console.WriteLine("Directory containing DB file is writeable {0}", dbDirFile.FullName);
            }

            // Make sure we can write to local copy of the DB file.
            if (dbFile.IsReadOnly)
            {
                //Console.WriteLine("Making writeable DB file {0}", dbFile.FullName);
                dbFile.IsReadOnly = false;
            }
            else
            {
                //Console.WriteLine("DB file is writeable {0}", dbFile.FullName);
            }
        }

        /// <summary>Gets a detailed grain report from a specified silo</summary>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="grainId">The grain id we are requesting information from</param>
        /// <param name="siloHandle">The target silo that should provide this information from it's cache</param>
        internal static Task<DetailedGrainReport> GetDetailedGrainReport(IInternalGrainFactory grainFactory, GrainId grainId, SiloHandle siloHandle)
        {
            // Use the siloAddress here, not the gateway address, since we may be targeting a silo on which we are not 
            // connected to the gateway
            var siloControl = grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, siloHandle.SiloAddress);
            return siloControl.GetDetailedGrainReport(grainId);
        }
    }
}
