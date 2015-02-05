using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Orleans;

// for checking the insert/upsert/read rate of AzureTable
namespace UnitTests.General
{
    public enum AzureTestOperationType
    {
        Insert = 0,
        Upsert,
        Read,
    }

    public class AzureTableTests
    {
        private static AzureTableTestManager GetTableManager()
        {
            // set these
            return AzureTableTestManager.GetManager("TMSLocalTesting6",
                                                    "DefaultEndpointsProtocol=https;AccountName=orleanstestdata;AccountKey=qFJFT+YAikJPCE8V5yPlWZWBRGns4oti9tqG6/oYAYFGI4kFAnT91HeiWMa6pddUzDcG5OAmri/gk7owTOQZ+A==");
        }

        public static void AzureTable_CheckOpRate(AzureTestOperationType opType)
        {
            ThreadPoolSettings();

            Console.WriteLine("Checking rate of {0} operations", opType);

            AzureTableTestManager mgr = GetTableManager();
            // warm up
            TestTableRate(mgr, "TMSLocalTesting2", 0, 100, opType);
            // run
            TestTableRate(mgr, "TMSLocalTesting2", 1000, 1000, opType);
            TestTableRate(mgr, "TMSLocalTesting2", 2000, 1000, opType);
            TestTableRate(mgr, "TMSLocalTesting2", 3000, 1000, opType);

            Console.WriteLine("Press any key to continue");
            Console.ReadKey();
        }

        private static void TestTableRate(AzureTableTestManager tblMgr, string prependMessage, int from, double numOfOps, AzureTestOperationType opType)
        {
            DateTime startedAt = DateTime.UtcNow;
            Random r = new Random(0); // use the same seed on each run so you read what you wrote

            try
            {
                var promises = new List<AsyncCompletion>();
                for (int i = from; i < from + numOfOps; i++)
                {
                    var e = new AzureTableTestEntry
                    {
                        PartitionKey = i.ToString(),
                        //RowKey = i.ToString("X")+"sdglkgjoijukdnfvkdfgfgjhnghmkmykuyukjvdfk",
                        RowKey = r.NextDouble() < 0.5 ? string.Format("0{0}", (-1 * i).ToString("X")) : string.Format("1{0:d33}", i),
                        Property1 = i,
                        Property2 = i
                    };

                    int capture = i;
                    //var promise1 = reminderTable.ReadRow(e.GrainId, e.ReminderName);
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

                    //var promise2 = promise1.ContinueWith(() => Console.WriteLine(tableName + " Done " + capture));
                    //promises.Add(promise2);
                    //Console.WriteLine(tableName + " Started " + capture);
                    //promises.Add(promise1);
                }
                Console.WriteLine(prependMessage + " Started all, now waiting...");
                AsyncCompletion.JoinAll(promises).Wait(TimeSpan.FromSeconds(500));
            }
            catch (Exception exc)
            {
                Console.WriteLine("{0} Exception caught {1}", prependMessage, exc);
            }
            TimeSpan dur = DateTime.UtcNow - startedAt;
            Console.WriteLine("{0}: {1} ops of type {2} in {3}, i.e., {4:f2} ops/sec", prependMessage, numOfOps, opType, dur, (numOfOps / dur.TotalSeconds));
        }

        static void ThreadPoolSettings()
        {
            ThreadPool.SetMinThreads(5000, 5000);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 1000;
            ServicePointManager.UseNagleAlgorithm = false;
        }

    }
}
