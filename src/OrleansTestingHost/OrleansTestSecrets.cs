﻿
using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Helper class to load storage credentials from <see cref="ORLEANS_TEST_SECRETS_FILE_NAME"/>. See also <seealso cref="StorageTestConstants"/>.
    /// </summary>
    public static class OrleansTestSecrets
    {
        // In order to specify your own test secrets you should:
        // 1) Create a file named OrleansTestSecrets.json with the below Contract data in it.
        //    {
        //      "DataConnectionString":"DefaultEndpointsProtocol=XXX",
        //      "EventHubConnectionString":"Endpoint=XXX"
        //    }
        //    Each of the above settings is optional, but not including either of them may cause some tests to fail.
        // 2) Define an environment variable ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH and point it to the folder where this file is located.
        // 
        // MSBuild unit test framework runs a script "SetupTestScript.cmd" (that is specified in Local.testsettings), which
        // copies ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH\OrleansTestSecrets.json into where the unit tests are run.
        // At runtime OrleansTestSecrets class will read this file and use your storage account key.

        /// <summary> The path of the file containing the storage credentials </summary>
        public const string ORLEANS_TEST_SECRETS_FILE_NAME = "OrleansTestSecrets.json";

        [Serializable]
        private class Contract
        {
            public string DataConnectionString { get; set; }
            public string EventHubConnectionString { get; set; }
        }

        /// <summary>
        /// Try to load the storage credentials from <see cref="ORLEANS_TEST_SECRETS_FILE_NAME"/> and
        /// store them in <see cref="StorageTestConstants.DataConnectionString"/> and <see cref="StorageTestConstants.EventHubConnectionString"/>
        /// </summary>
        /// <returns>True if success, false otherwise</returns>
        public static bool TryLoad()
        {
            try
            {
                if (!File.Exists(ORLEANS_TEST_SECRETS_FILE_NAME))
                {
                    string fileNotFoundMsg = string.Format("Did not find the {0} file or it was empty. Using Default Storage Data Connection String instead.", ORLEANS_TEST_SECRETS_FILE_NAME);
                    Console.Out.WriteLine(fileNotFoundMsg);
                    Trace.WriteLine(fileNotFoundMsg);
                    return false;
                }

                using (TextReader input = File.OpenText(ORLEANS_TEST_SECRETS_FILE_NAME))
                {
                    var contract = JsonConvert.DeserializeObject<Contract>(input.ReadToEnd());
                    StorageTestConstants.DataConnectionString = contract.DataConnectionString;
                    StorageTestConstants.EventHubConnectionString = contract.EventHubConnectionString;
                    return true;
                }
            }
            catch (Exception ex)
            {
                string fileFoundMsg = string.Format("Error loading {0}.  Exception: {1}", ORLEANS_TEST_SECRETS_FILE_NAME, ex);
                Console.Out.WriteLine(fileFoundMsg);
            }
            return false;
        }
    }
}
