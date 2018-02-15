using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions;
using Orleans.Transactions.Development;
using Orleans.Transactions.AzureStorage;
using Orleans.TestingHost.Utils;
using TestExtensions;

namespace Benchmarks.TransactionManager
{
    public class TransactionManagerBenchmarks
    {
        private const int TransactionsPerRun = 500000;
        private const int ConcurrentTransactionsTransactions = TransactionsPerRun/10;
        private static readonly TimeSpan LogMaintenanceInterval = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(10);
        List<TimeSpan> transactionBatchTimeouts = Enumerable.Range(0, ConcurrentTransactionsTransactions)
                                                    .Select(i => TransactionTimeout)
                                                    .ToList();

        public void RunAgainstMemory()
        {
            Factory<Task<ITransactionLogStorage>> storageFactory = () => Task.FromResult<ITransactionLogStorage>(new InMemoryTransactionLogStorage());
            Run(storageFactory).GetAwaiter().GetResult();
        }

        public void RunAgainstAzure()
        {
            Run(AzureStorageFactory).GetAwaiter().GetResult();
        }

        private async Task Run(Factory<Task<ITransactionLogStorage>> storageFactory)
        {
            ITransactionManager tm = new Orleans.Transactions.TransactionManager(new TransactionLog(storageFactory), Options.Create(new TransactionsOptions()), NullLoggerFactory.Instance, NullTelemetryProducer.Instance, Options.Create<SiloStatisticsOptions>(new SiloStatisticsOptions()), LogMaintenanceInterval);
            await tm.StartAsync();
            ITransactionManagerService tms = new TransactionManagerService(tm);
            Stopwatch sw;
            int success = 0;
            int failed = 0;

            sw = Stopwatch.StartNew();
            HashSet<long> transactionsInFlight = new HashSet<long>();
            int generatedTransactions = 0;
            while (generatedTransactions < TransactionsPerRun)
            {
                int generateCount = Math.Min(TransactionsPerRun - generatedTransactions, ConcurrentTransactionsTransactions - transactionsInFlight.Count);
                StartTransactionsResponse startResponse = await tms.StartTransactions(this.transactionBatchTimeouts.Take(generateCount).ToList());
                List<TransactionInfo> newTransactions = startResponse.TransactionId
                                                                .Select(this.MakeTransactionInfo)
                                                                .ToList();
                generatedTransactions += newTransactions.Count;
                Console.WriteLine($"Generated {newTransactions.Count} Transactions.");
                transactionsInFlight.UnionWith(newTransactions.Select(ti => ti.TransactionId));
                do
                {
                    Tuple<int, int> results = await CommitAndCount(tms, newTransactions, transactionsInFlight);
                    success += results.Item1;
                    failed += results.Item2;
                }
                while (transactionsInFlight.Count == ConcurrentTransactionsTransactions);
            }
            Console.WriteLine($"Generation Complete.");
            List<TransactionInfo> empty = new List<TransactionInfo>();
            while (transactionsInFlight.Count != 0)
            {
                Tuple<int, int> results = await CommitAndCount(tms, empty, transactionsInFlight);
                success += results.Item1;
                failed += results.Item2;
            }
            sw.Stop();
            Console.WriteLine($"{generatedTransactions} Transactions performed in {sw.ElapsedMilliseconds}ms.  Succeeded: {success}, Failed: {failed}.");
            Console.WriteLine($"{success * 1000 / sw.ElapsedMilliseconds} Transactions/sec.");
            await tm.StopAsync();
        }

        private async Task<Tuple<int,int>> CommitAndCount(ITransactionManagerService tms, List<TransactionInfo> newTransactions, HashSet<long> transactionsInFlight)
        {
            int success = 0;
            int failed = 0;
            CommitTransactionsResponse commitResponse = await tms.CommitTransactions(newTransactions, transactionsInFlight);
            if (commitResponse.CommitResult.Count != 0)
            {
                Console.WriteLine($"Commited {commitResponse.CommitResult.Count} Transactions.");
            }
            else await Task.Delay(10);
            foreach (KeyValuePair<long, CommitResult> kvp in commitResponse.CommitResult)
            {
                bool removed = transactionsInFlight.Remove(kvp.Key);
                if (!removed)
                {
                    Console.WriteLine($"Unrequested result: {kvp.Key}.");
                    continue;
                }
                if (kvp.Value.Success)
                {
                    success++;
                }
                else
                {
                    failed++;
                }
            }
            return Tuple.Create(success, failed);
        }

        private TransactionInfo MakeTransactionInfo(long transactionId)
        {
            var transactionInfo = new TransactionInfo(transactionId);
            transactionInfo.WriteSet[NoOpTransactionalResource.Instance] = 1;
            return transactionInfo;
        }

        [Serializable]
        private class NoOpTransactionalResource : ITransactionalResource
        {
            public static ITransactionalResource Instance => new NoOpTransactionalResource();

            public Task Abort(long transactionId)
            {
                return Task.CompletedTask;
            }

            public Task Commit(long transactionId)
            {
                return Task.CompletedTask;
            }

            public bool Equals(ITransactionalResource other)
            {
                return Object.Equals(this,other);
            }

            public Task<bool> Prepare(long transactionId, TransactionalResourceVersion? writeVersion, TransactionalResourceVersion? readVersion)
            {
                return Task.FromResult(true);
            }
        }

        private static async Task<ITransactionLogStorage> AzureStorageFactory()
        {
            var config = new ClientConfiguration();
            var environment = SerializationTestEnvironment.InitializeWithDefaults(config);
            var azureConfig = Options.Create(new AzureTransactionLogOptions()
            {
                // TODO: Find better way for test isolation.
                TableName = $"TransactionLog{((uint)Guid.NewGuid().GetHashCode()) % 100000}",
                ConnectionString = TestDefaultConfiguration.DataConnectionString
            });
            AzureTransactionLogStorage storage = new AzureTransactionLogStorage(environment.SerializationManager, azureConfig);
            await storage.Initialize();
            return storage;
        }
    }

}