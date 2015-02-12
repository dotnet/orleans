using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.AzureUtils;
using Microsoft.WindowsAzure.StorageClient;
using System.Data.Services.Common;
using System.Text;
using UnitTests.Tester;

namespace UnitTests.StorageTests
{
    [TestClass]
    public class AzureTableDataManager
    {
        private string PartitionKey;
        private UnitTestAzureTableDataManager manager;

        [TestInitialize]
        public void TestInitialize()
        {
            UnitTestUtils.ConfigureThreadPoolSettingsForStorageTests();

            // Pre-create table, if required
            manager = new UnitTestAzureTableDataManager(TestConstants.DataConnectionString);

            PartitionKey = "AzureTableTests-" + Guid.NewGuid();
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Storage"), TestCategory("Stress")]
        public void AzureTableDataManager_WriteAlot_SinglePartition()
        {
            const string testName = "AzureTable_WriteAlot_SinglePartition";
            const int iterations = 2000;
            const int batchSize = 1000;
            const int numPartitions = 1;

            // Write some data
            WriteAlot_Async(testName, numPartitions, iterations, batchSize);
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Storage"), TestCategory("Stress")]
        public void AzureTableDataManager_WriteAlot_MultiPartition()
        {
            const string testName = "AzureTable_WriteAlot_MultiPartition";
            const int iterations = 2000;
            const int batchSize = 1000;
            const int numPartitions = 100;

            // Write some data
            WriteAlot_Async(testName, numPartitions, iterations, batchSize);
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Storage"), TestCategory("Stress")]
        public void AzureTableDataManager_ReadAll_SinglePartition()
        {
            const string testName = "AzureTable_ReadAll";
            const int iterations = 1000;

            // Write some data
            WriteAlot_Async(testName, 1, iterations, iterations);

            Stopwatch sw = Stopwatch.StartNew();

            var data = manager.ReadAllDataAsync(PartitionKey)
                .WaitForResultWithThrow(AzureTableDefaultPolicies.TableCreationTimeout);

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
                var promise = manager.WriteDataAsync(partitionKey, rowKey, rowKey);
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
