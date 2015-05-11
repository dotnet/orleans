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
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.AzureUtils;
using Orleans.TestingHost;

namespace UnitTests.StorageTests
{
    [TestClass]
    public class AzureTableDataManagerStressTests
    {
        private string PartitionKey;
        private UnitTestAzureTableDataManager manager;

        [TestInitialize]
        public void TestInitialize()
        {
            TestingUtils.ConfigureThreadPoolSettingsForStorageTests();

            // Pre-create table, if required
            manager = new UnitTestAzureTableDataManager(StorageTestConstants.DataConnectionString);

            PartitionKey = "AzureTableDataManagerStressTests-" + Guid.NewGuid();
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Storage"), TestCategory("Stress")]
        public void AzureTableDataManagerStressTests_WriteAlot_SinglePartition()
        {
            const string testName = "AzureTableDataManagerStressTests_WriteAlot_SinglePartition";
            const int iterations = 2000;
            const int batchSize = 1000;
            const int numPartitions = 1;

            // Write some data
            WriteAlot_Async(testName, numPartitions, iterations, batchSize);
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Storage"), TestCategory("Stress")]
        public void AzureTableDataManagerStressTests_WriteAlot_MultiPartition()
        {
            const string testName = "AzureTableDataManagerStressTests_WriteAlot_MultiPartition";
            const int iterations = 2000;
            const int batchSize = 1000;
            const int numPartitions = 100;

            // Write some data
            WriteAlot_Async(testName, numPartitions, iterations, batchSize);
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Storage"), TestCategory("Stress")]
        public void AzureTableDataManagerStressTests_ReadAll_SinglePartition()
        {
            const string testName = "AzureTableDataManagerStressTests_ReadAll";
            const int iterations = 1000;

            // Write some data
            WriteAlot_Async(testName, 1, iterations, iterations);

            Stopwatch sw = Stopwatch.StartNew();

            var data = manager.ReadAllTableEntriesForPartitionAsync(PartitionKey)
                .WaitForResultWithThrow(AzureTableDefaultPolicies.TableCreationTimeout).Select(tuple => tuple.Item1);

            sw.Stop();
            int count = data.Count();
            Console.WriteLine("AzureTable_ReadAll completed. ReadAll {0} entries in {1} at {2} RPS", count, sw.Elapsed, count / sw.Elapsed.TotalSeconds);

            Assert.IsTrue(count >= iterations, "ReadAllshould return some data: Found={0}", count);
        }

        private void WriteAlot_Async(string testName, int numPartitions, int iterations, int batchSize)
        {
            Console.WriteLine("Iterations={0}, Batch={1}, Partitions={2}", iterations, batchSize, numPartitions);
            List<Task> promises = new List<Task>();
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                string partitionKey = PartitionKey;
                if (numPartitions > 1) partitionKey += (i % numPartitions);
                string rowKey = i.ToString(CultureInfo.InvariantCulture);
                
                UnitTestAzureTableData dataObject = new UnitTestAzureTableData();
                dataObject.PartitionKey = partitionKey;
                dataObject.RowKey = rowKey;
                dataObject.StringData = rowKey;
                var promise = manager.UpsertTableEntryAsync(dataObject);
                promises.Add(promise);
                if ((i % batchSize) == 0 && i > 0)
                {
                    Task.WhenAll(promises).WaitWithThrow(AzureTableDefaultPolicies.TableCreationTimeout);
                    promises.Clear();
                    Console.WriteLine("{0} has written {1} rows in {2} at {3} RPS",
                        testName, i, sw.Elapsed, i / sw.Elapsed.TotalSeconds);
                }
            }
            Task.WhenAll(promises).WaitWithThrow(AzureTableDefaultPolicies.TableCreationTimeout);
            sw.Stop();
            Console.WriteLine("{0} completed. Wrote {1} entries to {2} partition(s) in {3} at {4} RPS",
                testName, iterations, numPartitions, sw.Elapsed, iterations / sw.Elapsed.TotalSeconds);
        }
    }
}
