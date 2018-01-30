using System;
using Xunit;
using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Transactions.Tests;
using Orleans.TestingHost.Utils;
using TestExtensions;

namespace Orleans.Transactions.AzureStorage.Tests
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
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.AddAzureTableStorageProvider(TransactionTestConstants.TransactionStore);
            });
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        private class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                var id = (uint) Guid.NewGuid().GetHashCode();
                hostBuilder.UseInClusterTransactionManager()
                    .UseAzureTransactionLog(options => {
                        // TODO: Find better way for test isolation.  Possibly different partition keys.
                        options.TableName = $"TransactionLog{id % 100000:X}";
                        options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                    })
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
