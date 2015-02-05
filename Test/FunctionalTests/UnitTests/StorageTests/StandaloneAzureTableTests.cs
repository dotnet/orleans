using System;
using System.Collections.Generic;
using System.Globalization;
using Orleans;
using Orleans.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

// for checking the insert/upsert/read rate of AzureTable
namespace UnitTests.StorageTests
{
    internal enum AzureTestOperationType
    {
        Insert = 0,
        Upsert,
        Read,
    }

    [TestClass]
    public class StandaloneAzureTableTests
    {
        private static readonly Lazy<StandaloneAzureTableTestManager> manager = new Lazy<StandaloneAzureTableTestManager>(
            () => StandaloneAzureTableTestManager.GetManager(
                    "TMSLocalTesting6",
                    TestConstants.DataConnectionString)
        );

        [TestMethod, TestCategory("Azure"), TestCategory("Table"), TestCategory("Performance")]
        public void AzureTable_CheckOpRate_Insert()
        {
            AzureTable_CheckOpRate(AzureTestOperationType.Insert);
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Table"), TestCategory("Performance")]
        public void AzureTable_CheckOpRate_Upsert()
        {
            AzureTable_CheckOpRate(AzureTestOperationType.Upsert);
        }

        [TestMethod, TestCategory("Azure"), TestCategory("Table"), TestCategory("Performance")]
        public void AzureTable_CheckOpRate_Read()
        {
            AzureTable_CheckOpRate(AzureTestOperationType.Read);
        }

        private void AzureTable_CheckOpRate(AzureTestOperationType opType)
        {
            UnitTestBase.ConfigureClientThreadPoolSettingsForStorageTests();

            Console.WriteLine("Checking rate of {0} operations", opType);

            StandaloneAzureTableTestManager mgr = manager.Value;
            // warm up
            TestTableRate(mgr, "TMSLocalTesting2", 0, 100, opType);
            // run
            TestTableRate(mgr, "TMSLocalTesting2", 1000, 1000, opType);
            TestTableRate(mgr, "TMSLocalTesting2", 2000, 1000, opType);
            TestTableRate(mgr, "TMSLocalTesting2", 3000, 1000, opType);

            //Console.WriteLine("Press any key to continue");
            //Console.ReadKey();
        }

        private static void TestTableRate(StandaloneAzureTableTestManager tblMgr, string prependMessage, int from, double numOfOps, AzureTestOperationType opType)
        {
            DateTime startedAt = DateTime.UtcNow;
            Random r = new Random(0); // use the same seed on each run so you read what you wrote

            try
            {
                var promises = new List<Task>();
                for (int i = from; i < from + numOfOps; i++)
                {
                    var e = new StandaloneAzureTableTestEntry
                    {
                        PartitionKey = i.ToString(CultureInfo.InvariantCulture),
                        RowKey = r.NextDouble() < 0.5 ? string.Format("0{0}", (-1 * i).ToString("X")) : string.Format("1{0:d33}", i),
                        Property1 = i,
                        Property2 = i
                    };

                    switch (opType)
                    {
                        case AzureTestOperationType.Insert:
                            var promiseIn = tblMgr.InsertTestEntry(e);
                            promises.Add(promiseIn);
                            break;

                        case AzureTestOperationType.Upsert:
                            var promiseUp = tblMgr.UpsertRow(e);
                            promises.Add(promiseUp);
                            break;

                        case AzureTestOperationType.Read:
                            var promiseRead = tblMgr.FindTestEntry(e.PartitionKey, e.RowKey);
                            promises.Add(promiseRead);
                            break;
                    }
                }
                Console.WriteLine(prependMessage + " Started all, now waiting...");
                Task.WhenAll(promises).WaitWithThrow(TimeSpan.FromSeconds(500));
            }
            catch (Exception exc)
            {
                Console.WriteLine("{0} Exception caught {1}", prependMessage, exc);
            }
            TimeSpan dur = DateTime.UtcNow - startedAt;
            Console.WriteLine("{0}: {1} ops of type {2} in {3}, i.e., {4:f2} ops/sec", prependMessage, numOfOps, opType, dur, (numOfOps / dur.TotalSeconds));
        }
    }
}
