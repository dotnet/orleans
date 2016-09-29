
using System;

namespace Orleans.TestingHost
{
    /// <summary> Class to store storage constants used in tests </summary>
    public static class StorageTestConstants
    {
        // Set DefaultStorageDataConnectionString to your actual Azure Storage DataConnectionString, or load if from OrleansTestSecrets
        // private const string DefaultStorageDataConnectionString ="DefaultEndpointsProtocol=https;AccountName=XXX;AccountKey=YYY"

        /// <summary> Get or set the connection string to use. By default uses <see cref="DEFAULT_STORAGE_DATA_CONNECTION_STRING"/> value. </summary>
        public static string DataConnectionString { get; set; }

        /// <summary> The default storage connection string </summary>
        private const string DEFAULT_STORAGE_DATA_CONNECTION_STRING = "UseDevelopmentStorage=true";

        /// <summary> Get or set the connection string to event hub </summary>
        public static string EventHubConnectionString { get; set; }

        static StorageTestConstants()
        {
            if (DataConnectionString != null)
            {
                return;
            }

            if (!OrleansTestSecrets.TryLoad())
            {
                DataConnectionString = DEFAULT_STORAGE_DATA_CONNECTION_STRING;
            }
        }

        /// <summary> Return true if the configured connection string is targeting the storage emulator </summary>
        public static bool UsingAzureLocalStorageEmulator
        {
            get
            {
                string connString = DataConnectionString;
                bool usingLocalWAS = DEFAULT_STORAGE_DATA_CONNECTION_STRING.Equals(connString, StringComparison.OrdinalIgnoreCase);
                //string msg = string.Format("Using Azure local storage emulator = {0}", usingLocalWAS);
                //Console.WriteLine(msg);
                //Trace.WriteLine(msg);
                return usingLocalWAS;
            }
        }

        /// <summary> Get the ZooKeeper connection string </summary>
        /// <returns></returns>
        public static string GetZooKeeperConnectionString()
        {
            return "127.0.0.1:2181";
        }
    }
}
