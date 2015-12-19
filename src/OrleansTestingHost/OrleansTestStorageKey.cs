using System;
using System.Diagnostics;
using System.IO;

namespace Orleans.TestingHost
{
    public static class OrleansTestStorageKey
    {
        //
        // This is depricated. Please use OrleansTestSecrets
        //
        // In order to specify your own Azure Storage DataConnectionString you should:
        // 1) Create a file named OrleansTestStorageKey.txt and put one line with your storage key there, without "", like this:
        // DefaultEndpointsProtocol=https;AccountName=XXX;AccountKey=YYY
        // 2) Define an environment variable ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH and point it to the folder where this file is located.
        // 
        // MSBuild unit test framework runs a script "SetupTestScript.cmd" (that is specified in Local.testsettings), which
        // copies ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH\OrleansTestStorageKey.txt into where the unit tests are run.
        // At runtime StorageTestConstants class will read this file and use your storage account key.
        // 
        // Alternativerly, instead of using a file, you can just:
        // Set DefaultStorageDataConnectionString to your actual Azure Storage DataConnectionString
        // private const string DefaultStorageDataConnectionString ="DefaultEndpointsProtocol=https;AccountName=XXX;AccountKey=YYY"
        public const string ORLEANS_TEST_STORAGE_KEY_FILE_NAME = "OrleansTestStorageKey.txt";

        public static bool TryLoad()
        {
            try
            {
                if (!File.Exists(ORLEANS_TEST_STORAGE_KEY_FILE_NAME))
                {
                    string fileNotFoundMsg = string.Format("Did not find the {0} file or it was empty. Using Default Storage Data Connection String instead.", ORLEANS_TEST_STORAGE_KEY_FILE_NAME);
                    Console.Out.WriteLine(fileNotFoundMsg);
                    Trace.WriteLine(fileNotFoundMsg);
                    return false;
                }

                using (TextReader input = File.OpenText(ORLEANS_TEST_STORAGE_KEY_FILE_NAME))
                {
                    string line = input.ReadToEnd();
                    line = line.Trim();
                    if (!String.IsNullOrEmpty(line))
                    {
                        string fileFoundMsg = string.Format("Found the {0} file and using the Storage Key from there.", ORLEANS_TEST_STORAGE_KEY_FILE_NAME);
                        Console.Out.WriteLine(fileFoundMsg);
                        Trace.WriteLine(fileFoundMsg);
                        StorageTestConstants.DataConnectionString = line;
                    }
                }

                return !string.IsNullOrWhiteSpace(StorageTestConstants.DataConnectionString);
            }
            catch (Exception ex)
            {
                string fileFoundMsg = string.Format("Error loading {0}.  Exception: {1}", ORLEANS_TEST_STORAGE_KEY_FILE_NAME, ex);
                Console.Out.WriteLine(fileFoundMsg);
            }
            return false;
        }
    }
}
