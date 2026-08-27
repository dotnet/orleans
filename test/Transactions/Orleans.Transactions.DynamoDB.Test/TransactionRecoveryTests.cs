using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using Orleans.Transactions.TestKit;
using Orleans.Transactions.TestKit.xUnit;
using Tester;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.DynamoDB.Tests
{
    /// <summary>
    /// Tests for transaction recovery after silo failures with DynamoDB clustering.
    /// </summary>
    [TestSuite("Functional")]
    [TestProvider("DynamoDB")]
    [TestArea("Transactions")]
    [TestCategory("DynamoDB"), TestCategory("Transactions"), TestCategory("Functional")]
    public class TransactionRecoveryTests : TestClusterPerTest
    {
        private TransactionRecoveryTestsRunnerxUnit testRunner = null!;
        private readonly ITestOutputHelper helper;

        public TransactionRecoveryTests(ITestOutputHelper helper)
        {
            this.EnsurePreconditionsMet();
            this.helper = helper;
        }

        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }
            this.testRunner = new TransactionRecoveryTestsRunnerxUnit(this.HostedCluster, helper);
        }

        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            if (!AWSTestConstants.IsDynamoDbAvailable)
            {
                throw Xunit.Sdk.SkipException.ForSkip("DynamoDB is not configured");
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.AddSiloBuilderConfigurator<TestFixture.SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloBuilderConfiguratorUsingDynamoDBClustering>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfiguratorUsingDynamoDBClustering>();
        }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, 30)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, 20)]
        public Task TransactionWillRecoverAfterRandomSiloGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloGracefulShutdown(
                transactionTestGrainClassName,
                concurrent,
                TestContext.Current.CancellationToken);
        }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, 30)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, 20)]
        public Task TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(
                transactionTestGrainClassName,
                concurrent,
                TestContext.Current.CancellationToken);
        }

        [Fact]
        public Task TransactionWillRecoverAfterManagerWait()
        {
            return this.testRunner.TransactionWillRecoverAfterManagerWait(
                TransactionTestConstants.SingleStateTransactionalGrain);
        }

        [Fact]
        public Task TransactionWillRecoverAfterRemotePreparePersisted()
        {
            return this.testRunner.TransactionWillRecoverAfterRemotePreparePersisted(
                TransactionTestConstants.SingleStateTransactionalGrain);
        }

        [Fact]
        public Task TransactionWillRecoverAfterLocalCommitStored()
        {
            return this.testRunner.TransactionWillRecoverAfterLocalCommitStored(
                TransactionTestConstants.SingleStateTransactionalGrain);
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
