using System;
using Xunit;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Transactions.Tests;
using Orleans.TestingHost.Utils;
using TestExtensions;
using Microsoft.Extensions.Logging;

namespace Orleans.Transactions.AzureStorage.Tests.DistributedTM
{
    public class TestFixture : BaseTestClusterFixture
    {
        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            CheckForAzureStorage(TestDefaultConfiguration.DataConnectionString);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                var id = (uint)Guid.NewGuid().GetHashCode() % 100000;
                hostBuilder
                    .ConfigureLogging(builder => builder.AddFilter("SingleStateTransactionalGrain.data", LogLevel.Trace))
                    .ConfigureLogging(builder => builder.AddFilter("TransactionAgent", LogLevel.Trace))
                    .AddAzureTableGrainStorage(TransactionTestConstants.TransactionStore, options =>
                    {
                        options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                    })
                    .UseDistributedTM();
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
