using Amazon.DynamoDBv2.Model;
using Orleans.TestingHost.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.StorageTests.AWSUtils
{
    public class DynamoDBStorageStressTests : IClassFixture<DynamoDBStorageTestsFixture>
    {
        private readonly ITestOutputHelper output;
        private string PartitionKey;
        private UnitTestDynamoDBStorage manager;

        public DynamoDBStorageStressTests(DynamoDBStorageTestsFixture fixture, ITestOutputHelper output)
        {
            this.output = output;
            TestingUtils.ConfigureThreadPoolSettingsForStorageTests();

            manager = fixture.DataManager;
            PartitionKey = "PK-DynamoDBDataManagerStressTests-" + Guid.NewGuid();
        }

        [Fact, TestCategory("AWS"), TestCategory("Storage"), TestCategory("Stress")]
        public void DynamoDBDataManagerStressTests_WriteAlot_SinglePartition()
        {
            const string testName = "DynamoDBDataManagerStressTests_WriteAlot_SinglePartition";
            const int iterations = 2000;
            const int batchSize = 1000;
            const int numPartitions = 1;

            // Write some data
            WriteAlot_Async(testName, numPartitions, iterations, batchSize);
        }

        [Fact, TestCategory("AWS"), TestCategory("Storage"), TestCategory("Stress")]
        public void DynamoDBDataManagerStressTests_WriteAlot_MultiPartition()
        {
            const string testName = "DynamoDBDataManagerStressTests_WriteAlot_MultiPartition";
            const int iterations = 2000;
            const int batchSize = 1000;
            const int numPartitions = 100;

            // Write some data
            WriteAlot_Async(testName, numPartitions, iterations, batchSize);
        }

        [Fact, TestCategory("AWS"), TestCategory("Storage"), TestCategory("Stress")]
        public void DynamoDBDataManagerStressTests_ReadAll_SinglePartition()
        {
            const string testName = "DynamoDBDataManagerStressTests_ReadAll";
            const int iterations = 1000;

            // Write some data
            WriteAlot_Async(testName, 1, iterations, iterations);

            Stopwatch sw = Stopwatch.StartNew();

            var keys = new Dictionary<string, AttributeValue> { { ":PK", new AttributeValue(PartitionKey) } };
            var data = manager.QueryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, keys, $"PartitionKey = :PK", item => new UnitTestDynamoDBTableData(item)).Result;

            sw.Stop();
            int count = data.Count();
            output.WriteLine("DynamoDBDataManagerStressTests_ReadAll completed. ReadAll {0} entries in {1} at {2} RPS", count, sw.Elapsed, count / sw.Elapsed.TotalSeconds);

            //Assert.True(count >= iterations, $"ReadAllshould return some data: Found={count}");
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

                UnitTestDynamoDBTableData dataObject = new UnitTestDynamoDBTableData();
                dataObject.PartitionKey = partitionKey;
                dataObject.RowKey = rowKey;
                dataObject.StringData = rowKey;
                var promise = manager.UpsertEntryAsync(UnitTestDynamoDBStorage.INSTANCE_TABLE_NAME, DynamoDBStorageTests.GetKeys(dataObject), DynamoDBStorageTests.GetValues(dataObject));
                promises.Add(promise);
                if ((i % batchSize) == 0 && i > 0)
                {
                    Task.WhenAll(promises);
                    promises.Clear();
                    output.WriteLine("{0} has written {1} rows in {2} at {3} RPS",
                        testName, i, sw.Elapsed, i / sw.Elapsed.TotalSeconds);
                }
            }
            Task.WhenAll(promises);
            sw.Stop();
            output.WriteLine("{0} completed. Wrote {1} entries to {2} partition(s) in {3} at {4} RPS",
                testName, iterations, numPartitions, sw.Elapsed, iterations / sw.Elapsed.TotalSeconds);
        }
    }
}
