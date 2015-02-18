using System;
using System.Collections.Generic;
using System.Data.Services.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using LoadTestBase;
using LoadTestGrainInterfaces;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

namespace Orleans.Tests.Persistence
{
    public class AzureStorageDirectWorker : OrleansClientWorkerBase, IPersistenceWorker
    {
        // Note: We need to extend OrleansClientWorkerBase instead of just DirectClientWorkerBase
        // because now that GrainId is internal, we can't get the key string values to use for 
        // partition keys without creating grain references.

        // We want to use the exact same type of partition key values as the the PersistenceGrainWorker
        // test case uses, to allow direct comparison of result numbers.

        internal static bool Verbose = false;

        private string testName;
        private long numGrains;
        private AzureTableTestManager tableManager;
        private string[] partitionKeys;
        private PartitionKeyType partitionKeyType;
        public TimeSpan AggregateLatency { get; private set; } // sum of all latencies
        private readonly object lockable = new object();

        private static readonly string ConnectionString =
            "DefaultEndpointsProtocol=https;AccountName=orleans2data1;AccountKey=STRSp/pi8OGs9tT5tbc7KDLFonb7rk5pZ8ULKCJNzbXNlFz05sidJM3bkm6+XqxMBhXiu4yQQt26nI3VwRE+Fw==";

        public void ApplicationInitialize(string name, long nGrains, PartitionKeyType partKeyType)
        {
            this.testName = name;
            this.numGrains = nGrains;
            this.partitionKeys = new string[numGrains];
            this.partitionKeyType = partKeyType;
            this.AggregateLatency = TimeSpan.Zero;

            if ((numGrains % _nThreads) != 0)
            {
                throw new ArgumentException(String.Format("numGrains should be devisable by NUM_THREADS for AzureStorageDirectWorker. numGrains {0}, Config is: {1}", numGrains, this));
            }

            partitionKeys = GetPartitionKeys(numGrains).ToArray();

            ThreadPoolSettings();
            tableManager = AzureTableTestManager.GetManager(ConnectionString);
        }

        protected override async Task IssueRequest(int requestNumber, int threadNumber)
        {
            string partitionKeyStr = null;
            string rowKey = null;

            if (partitionKeyType == PartitionKeyType.DifferentPerThread)
            {
                // make sure every thread is writing to its own grain.
                long range = numGrains / _nThreads;
                long myRangeStart = range * threadNumber;
                long index = myRangeStart + requestNumber % range;
                partitionKeyStr = partitionKeys[index];
                rowKey = "GrainTypeSameStringForAllPartitionKeys";
            }
            else if (partitionKeyType == PartitionKeyType.SharedBetweenThreads)
            {
                // Concurent writes from different threads to the same table row.
                partitionKeyStr = partitionKeys[requestNumber % numGrains];
                rowKey = "GrainTypeSameStringForAllPartitionKeys";
            }
            else if (partitionKeyType == PartitionKeyType.UniquePerRequest)
            {
                // Partition key per request
                string partitionKey = requestNumber.ToString(CultureInfo.InvariantCulture);
                partitionKeyStr = partitionKey;
                long bkt = requestNumber % numGrains;
                rowKey = bkt.ToString(CultureInfo.InvariantCulture);
            }

            if (Verbose)
            {
                WriteProgress(
                    "{0}.IssueRequest: PartitionKey #{1} RowKey #{2} request #{3}",
                    testName,
                    partitionKeyStr,
                    rowKey,
                    requestNumber);
            }

            // random Guid for partition key, like in the grain id case, same row key for same grain id.
            var data = new AzureTableTestEntry
            {
                //PartitionKey = requestNumber.ToString(CultureInfo.InvariantCulture),
                //RowKey = bkt.ToString(CultureInfo.InvariantCulture),

                PartitionKey = partitionKeyStr,
                RowKey = rowKey,
                Data = threadNumber.ToString(CultureInfo.InvariantCulture) + ":" + requestNumber.ToString(CultureInfo.InvariantCulture),
            };

            Stopwatch sw = Stopwatch.StartNew();

            await tableManager.UpsertRow(data);

            sw.Stop();
            lock (lockable)
            {
                AggregateLatency += sw.Elapsed;
            }
        }

        private static void ThreadPoolSettings()
        {
            const int NumDotNetPoolThreads = 200;
            ThreadPool.SetMinThreads(NumDotNetPoolThreads, NumDotNetPoolThreads);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = NumDotNetPoolThreads; // 1000;
            ServicePointManager.UseNagleAlgorithm = false;
        }

        private static List<string> GetPartitionKeys(long numGrains)
        {
            List<string> keys = new List<string>();
            for (int i = 0; i < numGrains; i++)
            {
                keys.Add(((GrainReference) PersistenceLoadTestGrainFactory.GetGrain(Guid.NewGuid())).ToKeyString()); // random Guids.
                //partitionKeys[i] = GrainId.GetGrainId(-26872583, i);
            }
            //List<Task<GrainId>> promises = new List<Task<GrainId>>();
            //for (int i = 0; i < numGrains; i++)
            //{
            //    IPersistenceLoadTestGrain grain = PersistenceLoadTestGrainFactory.GetGrain(i);
            //    Task<GrainId> t = grain.GetGrainId();
            //    promises.Add(t);
            //}
            //Task.WaitAll(promises.ToArray());
            //for (int i = 0; i < numGrains; i++)
            //{
            //    keys.Add(promises[i].Result);
            //}
            return keys;
        }
    }

    //[DataServiceKey("PartitionKey", "RowKey")]
    public class AzureTableTestEntry : TableEntity
    {
        public string Data { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.Append("TestEntry [");
            sb.Append(" Data=").Append(Data);
            sb.Append("]");

            return sb.ToString();
        }
    }

    internal class AzureTableTestManager : AzureTableDataManager<AzureTableTestEntry>
    {
        private const string INSTANCE_TABLE_NAME_PREFIX = "TestPersistence";
        private static AzureTableTestManager singleton;
        private readonly static object classLock = new object();
        private static readonly TimeSpan tableCreationTimeout = TimeSpan.FromMinutes(3);

        public static AzureTableTestManager GetManager(string storageConnectionString)
        {
            lock (classLock)
            {
                if (singleton == null)
                {
                    TraceLogger.Initialize(new ClientConfiguration());
                    singleton = new AzureTableTestManager(storageConnectionString);
                    LoadTestDriverBase.WriteProgress(String.Format("AzureTableTestManager is using DataConnectionString: {0}", ConfigUtilities.PrintDataConnectionInfo(storageConnectionString)));
                    bool ok = singleton.InitTableAsync().Wait(tableCreationTimeout);
                    if (!ok)
                    {
                        throw new TimeoutException("Timeout waiting for table initialization for " + tableCreationTimeout);
                    }
                }
            }
            return singleton;
        }

        private AzureTableTestManager(string storageConnectionString)
            : base(INSTANCE_TABLE_NAME_PREFIX, storageConnectionString)
        {
        }

        public async Task UpsertRow(AzureTableTestEntry testEntry)
        {
            try
            {
                string eTag = await UpsertTableEntryAsync(testEntry);
            }
            catch (Exception exc)
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                string errMsg = "UpsertRow failed";
                if (AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus))
                {
                    errMsg += string.Format(" with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                    LoadTestDriverBase.WriteProgress(errMsg);
                    if (AzureStorageUtils.IsContentionError(httpStatusCode)) return; // false;
                }
                throw new AggregateException(errMsg, exc);
            }
        }
    }
}
