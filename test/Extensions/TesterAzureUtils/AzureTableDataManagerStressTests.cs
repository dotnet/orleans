using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Orleans.TestingHost.Utils;
using Xunit;
using Xunit.Abstractions;
using Orleans.Internal;
using AzureStoragePolicyOptions = Orleans.Clustering.AzureStorage.AzureStoragePolicyOptions;

namespace Tester.AzureUtils
{
    [TestCategory("AzureStorage"), TestCategory("Storage"), TestCategory("Stress")]
    public class AzureTableDataManagerStressTests : AzureStorageBasicTests
    {
        private readonly ITestOutputHelper output;
        private string PartitionKey;
        private UnitTestAzureTableDataManager manager;

        public AzureTableDataManagerStressTests(ITestOutputHelper output)
        {
            this.output = output;
            TestingUtils.ConfigureThreadPoolSettingsForStorageTests();

            // Pre-create table, if required
            manager = new UnitTestAzureTableDataManager();

            PartitionKey = "AzureTableDataManagerStressTests-" + Guid.NewGuid();
        }

        [SkippableFact]
        public void AzureTableDataManagerStressTests_WriteAlot_SinglePartition()
        {
            const string testName = "AzureTableDataManagerStressTests_WriteAlot_SinglePartition";
            const int iterations = 2000;
            const int batchSize = 1000;
            const int numPartitions = 1;

            // Write some data
            WriteAlot_Async(testName, numPartitions, iterations, batchSize);
        }

        [SkippableFact]
        public void AzureTableDataManagerStressTests_WriteAlot_MultiPartition()
        {
            const string testName = "AzureTableDataManagerStressTests_WriteAlot_MultiPartition";
            const int iterations = 2000;
            const int batchSize = 1000;
            const int numPartitions = 100;

            // Write some data
            WriteAlot_Async(testName, numPartitions, iterations, batchSize);
        }

        [SkippableFact]
        public void AzureTableDataManagerStressTests_ReadAll_SinglePartition()
        {
            const string testName = "AzureTableDataManagerStressTests_ReadAll";
            const int iterations = 1000;

            // Write some data
            WriteAlot_Async(testName, 1, iterations, iterations);

            Stopwatch sw = Stopwatch.StartNew();

            var data = manager.ReadAllTableEntriesForPartitionAsync(PartitionKey)
                .WaitForResultWithThrow(new AzureStoragePolicyOptions().CreationTimeout).Select(tuple => tuple.Entity);

            sw.Stop();
            int count = data.Count();
            output.WriteLine("AzureTable_ReadAll completed. ReadAll {0} entries in {1} at {2} RPS", count, sw.Elapsed, count / sw.Elapsed.TotalSeconds);

            Assert.True(count >= iterations, $"ReadAllshould return some data: Found={count}");
        }

        [SkippableFact]
        public void AzureTableDataManagerStressTests_ReadAllTableEntities()
        {
            const string testName = "AzureTableDataManagerStressTests_ReadAllTableEntities";
            const int iterations = 2000;

            // Write some data
            WriteAlot_Async(testName, 3, iterations, iterations);

            Stopwatch sw = Stopwatch.StartNew();

            var data = manager.ReadAllTableEntriesAsync()
                .WaitForResultWithThrow(new AzureStoragePolicyOptions().CreationTimeout).Select(tuple => tuple.Entity);

            sw.Stop();
            int count = data.Count();
            output.WriteLine("AzureTable_ReadAllTableEntities completed. ReadAll {0} entries in {1} at {2} RPS", count, sw.Elapsed, count / sw.Elapsed.TotalSeconds);

            Assert.True(count >= iterations, $"ReadAllshould return some data: Found={count}");

            sw = Stopwatch.StartNew();
            manager.ClearTableAsync().WaitWithThrow(new AzureStoragePolicyOptions().CreationTimeout);
            sw.Stop();
            output.WriteLine("AzureTable_ReadAllTableEntities clear. Cleared table of {0} entries in {1} at {2} RPS", count, sw.Elapsed, count / sw.Elapsed.TotalSeconds);
        }

        private void WriteAlot_Async(string testName, int numPartitions, int iterations, int batchSize)
        {
            output.WriteLine("Iterations={0}, Batch={1}, Partitions={2}", iterations, batchSize, numPartitions);
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
                    Task.WhenAll(promises).WaitWithThrow(new AzureStoragePolicyOptions().CreationTimeout);
                    promises.Clear();
                    output.WriteLine("{0} has written {1} rows in {2} at {3} RPS",
                        testName, i, sw.Elapsed, i / sw.Elapsed.TotalSeconds);
                }
            }
            Task.WhenAll(promises).WaitWithThrow(new AzureStoragePolicyOptions().CreationTimeout);
            sw.Stop();
            output.WriteLine("{0} completed. Wrote {1} entries to {2} partition(s) in {3} at {4} RPS",
                testName, iterations, numPartitions, sw.Elapsed, iterations / sw.Elapsed.TotalSeconds);
        }
    }
}
