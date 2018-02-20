using System;
using System.Collections.Generic;
using Xunit;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using AWSUtils.Tests.StorageTests;
using Orleans.Transactions.Tests;
using Orleans.Storage;

namespace Orleans.Transactions.DynamoDB.Tests
{
    public class TestFixture : BaseTestClusterFixture
    {
        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            CheckForDynamoDBStorage();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.ConfigureLegacyConfiguration((legacy) =>
            {
                legacy.ClusterConfiguration.Globals.RegisterStorageProvider<DynamoDBStorageProvider>(TransactionTestConstants.TransactionStore, new Dictionary<string, string>
                {
                    { "DataConnectionString", $"Service={AWSTestConstants.Service}" }
                });
            });
        }

        private class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                var id = (uint) Guid.NewGuid().GetHashCode() % 100000;
                hostBuilder
                    .UseInClusterTransactionManager()
                    .UseDynamoDBTransactionLog(options => {
                        // TODO: Find better way for test isolation.  Possibly different partition keys.
                        options.TableName = $"TransactionLog{id:X}";
                        options.ConnectionString = $"Service={AWSTestConstants.Service}";
                    })
                    .UseTransactionalState();
            }
        }

        public static void CheckForDynamoDBStorage()
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to AWS DynamoDB simulator");
        }
    }
}
