using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using Orleans.Transactions.TestKit;
using Orleans.Transactions.TestKit.xUnit;
using Tester;
using Tester.AzureUtils;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.AzureStorage.Tests
{
    /// <summary>
    /// Tests for transaction recovery after silo failures with Azure Storage clustering.
    /// </summary>
    [TestCategory("AzureStorage"), TestCategory("Transactions"), TestCategory("Functional")]
    [TestSuite("Functional")]
    [TestProvider("AzureStorage")]
    [TestArea("Transactions")]
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
            TestUtils.CheckForAzureStorage();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.AddSiloBuilderConfigurator<TestFixture.SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloBuilderConfiguratorUsingAzureClustering>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfiguratorUsingAzureClustering>();
        }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, 30)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, 20)]
        public Task TransactionWillRecoverAfterRandomSiloGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloGracefulShutdown(transactionTestGrainClassName, concurrent);
        }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, 30)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, 20)]
        public Task TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return this.testRunner.TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(transactionTestGrainClassName, concurrent);
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

        private class SiloBuilderConfiguratorUsingAzureClustering : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseAzureStorageClustering(options =>  options.ConfigureTestDefaults());
            }
        }

        private class ClientBuilderConfiguratorUsingAzureClustering : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.UseAzureStorageClustering(options => options.ConfigureTestDefaults());
            }
        }
    }
}
