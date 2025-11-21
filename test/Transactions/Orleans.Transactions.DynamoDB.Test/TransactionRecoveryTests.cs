using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using Orleans.Transactions.TestKit;
using Orleans.Transactions.TestKit.xUnit;
using Tester;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.DynamoDB.Tests
{
    /// <summary>
    /// Tests for transaction recovery after silo failures with DynamoDB clustering.
    /// </summary>
    [TestCategory("DynamoDB"), TestCategory("Transactions"), TestCategory("Functional")]
    public class TransactionRecoveryTests : TestClusterPerTest
    {
        private TransactionRecoveryTestsRunnerxUnit testRunner;
        private readonly ITestOutputHelper helper;

        public TransactionRecoveryTests(ITestOutputHelper helper)
        {
            this.EnsurePreconditionsMet();
            this.helper = helper;
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            this.testRunner = new TransactionRecoveryTestsRunnerxUnit(this.HostedCluster, helper);
        }

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
            builder.Options.InitialSilosCount = 4;
            builder.AddSiloBuilderConfigurator<TestFixture.SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloBuilderConfiguratorUsingDynamoDBClustering>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfiguratorUsingDynamoDBClustering>();
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, 30)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, 20)]
        public Task TransactionWillRecoverAfterRandomSiloGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloGracefulShutdown(transactionTestGrainClassName, concurrent);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, 30)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, 20)]
        public Task TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(transactionTestGrainClassName, concurrent);
        }

        private class SiloBuilderConfiguratorUsingDynamoDBClustering : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseDynamoDBClustering(options =>
                {
                    options.Service = AWSTestConstants.DynamoDbService;
                    options.SecretKey = AWSTestConstants.DynamoDbSecretKey;
                    options.AccessKey = AWSTestConstants.DynamoDbAccessKey;
                });
            }
        }

        private class ClientBuilderConfiguratorUsingDynamoDBClustering : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.UseDynamoDBClustering(options =>
                {
                    options.Service = AWSTestConstants.DynamoDbService;
                    options.SecretKey = AWSTestConstants.DynamoDbSecretKey;
                    options.AccessKey = AWSTestConstants.DynamoDbAccessKey;
                });
            }
        }
    }
}
