using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Orleans.Transactions.TestKit;
using Tester;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.DynamoDB.Tests
{
    public class TestFixture : BaseTestClusterFixture
    {
        public const string TableName = "TransactionStore";

        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            if (!AWSTestConstants.IsDynamoDbAvailable)
            {
                throw new SkipException("DynamoDB is not configured");
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .ConfigureServices(services => services.AddKeyedSingleton<IRemoteCommitService, RemoteCommitService>(TransactionTestConstants.RemoteCommitService))
                    .AddDynamoDBTransactionalStateStorage(TransactionTestConstants.TransactionStore, options =>
                    {
                        options.TableName = TableName;
                        options.Service = AWSTestConstants.DynamoDbService;
                        options.SecretKey = AWSTestConstants.DynamoDbSecretKey;
                        options.AccessKey = AWSTestConstants.DynamoDbAccessKey;
                    })
                    .UseTransactions();
            }
        }

        public class ClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .UseTransactions();
            }
        }
    }

    public class ControlledFaultInjectionTestFixture : BaseTestClusterFixture
    {
        public const string TableName = "TransactionStore";

        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            if (!AWSTestConstants.IsDynamoDbAvailable)
            {
                throw new SkipException("DynamoDB is not configured");
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddFaultInjectionDynamoDBTransactionalStateStorage(TransactionTestConstants.TransactionStore, options =>
                    {
                        options.TableName = TableName;
                        options.Service = AWSTestConstants.DynamoDbService;
                        options.SecretKey = AWSTestConstants.DynamoDbSecretKey;
                        options.AccessKey = AWSTestConstants.DynamoDbAccessKey;
                    })
                    .UseControlledFaultInjectionTransactionState()
                    .UseTransactions()
                    .ConfigureServices(svc =>
                    {
                        svc.AddScoped<ITransactionFaultInjector, SimpleAzureStorageExceptionInjector>()
                        .AddScoped<IControlledTransactionFaultInjector>(sp => sp.GetService<ITransactionFaultInjector>() as IControlledTransactionFaultInjector);
                    });
            }
        }
    }

    public class SkewedClockTestFixture : TestFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SkewedClockConfigurator>();
            base.ConfigureTestCluster(builder);
        }
    }


    public class RandomFaultInjectedTestFixture : TestFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<TxSiloBuilderConfigurator>();
            base.ConfigureTestCluster(builder);
        }

        public class TxSiloBuilderConfigurator : ISiloConfigurator
        {
            private static readonly double probability = 0.05;
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddFaultInjectionDynamoDBTransactionalStateStorage(TransactionTestConstants.TransactionStore, options =>
                    {
                        options.TableName = TableName;
                        options.Service = AWSTestConstants.DynamoDbService;
                        options.SecretKey = AWSTestConstants.DynamoDbSecretKey;
                        options.AccessKey = AWSTestConstants.DynamoDbAccessKey;
                    })
                    .UseTransactions()
                    .ConfigureServices(services => services.AddSingleton<ITransactionFaultInjector>(sp => new RandomErrorInjector(probability)));
            }
        }
    }

}
