using System;
using Xunit;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using AWSUtils.Tests.StorageTests;
using Orleans.Transactions.Tests;

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
        }

        private class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                var id = (uint) Guid.NewGuid().GetHashCode() % 100000;
                hostBuilder
                    .UseInClusterTransactionManager()
                    .AddDynamoDBGrainStorage(TransactionTestConstants.TransactionStore, options =>
                        {
                            options.Service = AWSTestConstants.Service;
                        })
                    .UseDynamoDBTransactionLog(options => 
                        {
                            // TODO: Find better way for test isolation.  Possibly different partition keys.
                            options.TableName = $"TransactionLog{id:X}";
                            options.Service = AWSTestConstants.Service;
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
