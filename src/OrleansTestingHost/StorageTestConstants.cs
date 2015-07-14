﻿/*
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
using System.Diagnostics;
using System.IO;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    public static class StorageTestConstants
    {
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
        public static string DataConnectionString { get; private set; }
        public const string ORLEANS_TEST_STORAGE_KEY_FILE_NAME = "OrleansTestStorageKey.txt";

        private const string DEFAULT_STORAGE_DATA_CONNECTION_STRING = "UseDevelopmentStorage=true";

        static StorageTestConstants()
        {
            if (DataConnectionString != null)
            {
                return; // already initialized
            }
            if (File.Exists(ORLEANS_TEST_STORAGE_KEY_FILE_NAME))
            {
                using (TextReader input = File.OpenText(ORLEANS_TEST_STORAGE_KEY_FILE_NAME))
                {
                    string line = input.ReadToEnd();
                    line = line.Trim();
                    if (!String.IsNullOrEmpty(line))
                    {
                        string fileFoundMsg = string.Format("Found the {0} file and using the Storage Key from there.", ORLEANS_TEST_STORAGE_KEY_FILE_NAME);
                        Console.Out.WriteLine(fileFoundMsg);
                        Trace.WriteLine(fileFoundMsg);
                        DataConnectionString = line;
                    }
                }
            }

            if (DataConnectionString != null) return;

            // If did not find the file, just use the DevelopmentStorage
            string fileNotFoundMsg = string.Format("Did not find the {0} file or it was empty. Using Default Storage Data Connection String instead.", ORLEANS_TEST_STORAGE_KEY_FILE_NAME);
            Console.Out.WriteLine(fileNotFoundMsg);
            Trace.WriteLine(fileNotFoundMsg);
            DataConnectionString = DEFAULT_STORAGE_DATA_CONNECTION_STRING;
        }

        public static string GetZooKeeperConnectionString()
        {
            return "127.0.0.1:2181";
        }
    }
}
