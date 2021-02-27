using System.Threading.Tasks;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public class TransactionRecoveryTestsRunnerxUnit : TransactionRecoveryTestsRunner
    {
        public TransactionRecoveryTestsRunnerxUnit(TestCluster cluster, ITestOutputHelper testOutput)
            :base(cluster, testOutput.WriteLine)
        {
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, 30)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, 20)]
        public override Task TransactionWillRecoverAfterRandomSiloGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return base.TransactionWillRecoverAfterRandomSiloGracefulShutdown(transactionTestGrainClassName, concurrent);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, 30)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, 20)]
        public override Task TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(string transactionTestGrainClassName, int concurrent)
        {
            return base.TransactionWillRecoverAfterRandomSiloUnGracefulShutdown(transactionTestGrainClassName, concurrent);
        }
    }
}
