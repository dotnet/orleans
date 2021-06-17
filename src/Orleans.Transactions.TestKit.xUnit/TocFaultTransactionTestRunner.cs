using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class TocFaultTransactionTestRunnerxUnit : TocFaultTransactionTestRunner
    {
        protected TocFaultTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task MultiGrainWriteTransactionWithCommitFailure(string grainStates, int grainCount)
        {
            return base.MultiGrainWriteTransactionWithCommitFailure(grainStates, grainCount);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task MultiGrainWriteTransactionWithCommitException(string grainStates, int grainCount)
        {
            return base.MultiGrainWriteTransactionWithCommitException(grainStates, grainCount);
        }
    }
}
