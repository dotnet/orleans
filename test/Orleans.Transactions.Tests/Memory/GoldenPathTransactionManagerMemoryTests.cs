
using System;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Development;
using Orleans.TestingHost.Utils;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class GoldenPathTransactionManagerMemoryTests : GoldenPathTransactionManagerTestRunner
    {
        private static readonly TimeSpan LogMaintenanceInterval = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan StorageDelay = TimeSpan.FromMilliseconds(30);

        public GoldenPathTransactionManagerMemoryTests(ITestOutputHelper output)
            : base(MakeTransactionManager(), LogMaintenanceInterval, StorageDelay, output)
        {
        }

        private static ITransactionManager MakeTransactionManager()
        {
            Factory<Task<ITransactionLogStorage>> storageFactory = () => Task.FromResult<ITransactionLogStorage>(new InMemoryTransactionLogStorage());
            ITransactionManager tm = new TransactionManager(new TransactionLog(storageFactory), Options.Create(new TransactionsOptions()), NullLoggerFactory.Instance, NullTelemetryProducer.Instance, Options.Create<SiloStatisticsOptions>(new SiloStatisticsOptions()), LogMaintenanceInterval);
            tm.StartAsync().GetAwaiter().GetResult();
            return tm;
        }
    }
}
