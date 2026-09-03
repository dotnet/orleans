using System.Threading.Tasks;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    /// <inheritdoc cref="TransactionRecoveryTestsRunner"/>
    public class TransactionRecoveryTestsRunnerxUnit : TransactionRecoveryTestsRunner
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionRecoveryTestsRunnerxUnit"/> class.
        /// </summary>
        /// <param name="cluster">The test cluster used to run recovery scenarios.</param>
        /// <param name="testOutput">The xUnit test output helper.</param>
        public TransactionRecoveryTestsRunnerxUnit(TestCluster cluster, ITestOutputHelper testOutput)
            : base(cluster, testOutput.WriteLine)
        {
        }

        /// <inheritdoc cref="TransactionRecoveryTestsRunner.TransactionWillRecoverAfterRandomSiloGracefulShutdown(string, int)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, 30)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, 20)]
        public override Task TransactionWillRecoverAfterRandomSiloGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return base.TransactionWillRecoverAfterRandomSiloGracefulShutdown(transactionTestGrainClassName, concurrent);
        }

        /// <inheritdoc cref="TransactionRecoveryTestsRunner.TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string, int)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, 30)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, 20)]
        public override Task TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return base.TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(transactionTestGrainClassName, concurrent);
        }

        /// <inheritdoc />
        protected override Task TransactionWillRecoverAfterRandomSiloFailure(
            string transactionTestGrainClassName,
            int concurrent,
            bool gracefulShutdown)
        {
            return base.TransactionWillRecoverAfterRandomSiloFailure(
                transactionTestGrainClassName,
                concurrent,
                gracefulShutdown,
                TestContext.Current.CancellationToken);
        }
    }
}
