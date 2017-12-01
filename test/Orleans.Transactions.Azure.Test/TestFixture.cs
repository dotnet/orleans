using System;
using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using Orleans.Transactions.Tests;
using Orleans.TestingHost.Utils;
using Xunit;

namespace Orleans.Transactions.Azure.Tests
{
    public class TestFixture : BaseTestClusterFixture
    {
        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            CheckForAzureStorage(TestDefaultConfiguration.DataConnectionString);
        }

        protected override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions();
            options.ClusterConfiguration.AddAzureTableStorageProvider(TransactionTestConstants.TransactionStore);
            options.UseSiloBuilderFactory<SiloBuilderFactory>();
            return new TestCluster(options);
        }

        private class SiloBuilderFactory : ISiloBuilderFactory
        {
            public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
            {
                return new SiloHostBuilder()
                    .ConfigureSiloName(siloName)
                    .UseConfiguration(clusterConfiguration)
                    .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, TestingUtils.CreateTraceFileName(siloName, clusterConfiguration.Globals.ClusterId)))
                    .UseInClusterTransactionManager(new TransactionsConfiguration())
                    .UseAzureTransactionLog(new AzureTransactionLogConfiguration() {
                        // TODO: Find better way for test isolation.  Possibly different partition keys.
                        TableName = $"TransactionLog{((uint)clusterConfiguration.Globals.ClusterId.GetHashCode())%100000}",
                        ConnectionString = TestDefaultConfiguration.DataConnectionString})
                    .UseTransactionalState();
            }
        }

        public static void CheckForAzureStorage(string dataConnectionString)
        {
            if (string.IsNullOrWhiteSpace(dataConnectionString))
            {
                throw new SkipException("No connection string found. Skipping");
            }

            bool usingLocalWAS = string.Equals(dataConnectionString, "UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase);

            if (!usingLocalWAS)
            {
                // Tests are using Azure Cloud Storage, not local WAS emulator.
                return;
            }

            //Starts the storage emulator if not started already and it exists (i.e. is installed).
            if (!StorageEmulator.TryStart())
            {
                throw new SkipException("Azure Storage Emulator could not be started.");
            }
        }
    }
}
