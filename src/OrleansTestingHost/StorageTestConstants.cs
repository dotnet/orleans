
using System;

namespace Orleans.TestingHost
{
    public static class StorageTestConstants
    {
        // Set DefaultStorageDataConnectionString to your actual Azure Storage DataConnectionString, or load if from OrleansTestSecrets
        // private const string DefaultStorageDataConnectionString ="DefaultEndpointsProtocol=https;AccountName=XXX;AccountKey=YYY"
        public static string DataConnectionString { get; set; }
        private const string DEFAULT_STORAGE_DATA_CONNECTION_STRING = "UseDevelopmentStorage=true";

        public static string EventHubConnectionString { get; set; }

        static StorageTestConstants()
        {
            if (DataConnectionString != null)
            {
                return;
            }

            if (!OrleansTestSecrets.TryLoad() && !OrleansTestStorageKey.TryLoad())
            {
                DataConnectionString = DEFAULT_STORAGE_DATA_CONNECTION_STRING;
            }
        }

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

        public static string GetZooKeeperConnectionString()
        {
            return "127.0.0.1:2181";
        }
    }
}
