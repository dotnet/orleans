﻿
using System;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Development;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class GoldenPathTransactionManagerMemoryTests : GoldenPathTransactionManagerTestRunner
    {
        private static readonly TimeSpan LogMaintenanceInterval = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan StorageDelay = TimeSpan.FromMilliseconds(1);

        public GoldenPathTransactionManagerMemoryTests(ITestOutputHelper output)
            : base(MakeTransactionManager(), LogMaintenanceInterval, StorageDelay, output)
        {
        }

        private static ITransactionManager MakeTransactionManager()
        {
            ILoggerFactory loggerFactory = new LoggerFactory();
            Factory<Task<ITransactionLogStorage>> storageFactory = () => Task.FromResult<ITransactionLogStorage>(new InMemoryTransactionLogStorage());
            ITransactionManager tm = new TransactionManager(new TransactionLog(storageFactory), Options.Create(new TransactionsConfiguration()), loggerFactory, LogMaintenanceInterval);
            tm.StartAsync().GetAwaiter().GetResult();
            return tm;
        }
    }
}
