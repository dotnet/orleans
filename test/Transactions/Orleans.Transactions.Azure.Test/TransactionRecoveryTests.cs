using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using Orleans.Transactions.TestKit;
using Orleans.Transactions.TestKit.xUnit;
using Tester;
using Tester.AzureUtils;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.AzureStorage.Tests
{
    /// <summary>
    /// Tests for transaction recovery after silo failures with Azure Storage clustering.
    /// </summary>
    [TestCategory("AzureStorage"), TestCategory("Transactions"), TestCategory("Functional")]
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
            TestUtils.CheckForAzureStorage();
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.AddSiloBuilderConfigurator<TestFixture.SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloBuilderConfiguratorUsingAzureClustering>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfiguratorUsingAzureClustering>();
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
