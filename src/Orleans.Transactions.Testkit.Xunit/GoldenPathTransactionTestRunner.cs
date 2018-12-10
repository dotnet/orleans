using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class GoldenPathTransactionTestRunnerxUnit : GoldenPathTransactionTestRunner
    {
        protected GoldenPathTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task SingleGrainReadTransaction(string grainStates)
        {
            return base.SingleGrainReadTransaction(grainStates);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task SingleGrainWriteTransaction(string grainStates)
        {
            return base.SingleGrainWriteTransaction(grainStates);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task MultiGrainWriteTransaction(string grainStates, int grainCount)
        {
            return base.MultiGrainWriteTransaction(grainStates, grainCount);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task MultiGrainReadWriteTransaction(string grainStates, int grainCount)
        {
            return base.MultiGrainReadWriteTransaction(grainStates, grainCount);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task RepeatGrainReadWriteTransaction(string grainStates, int grainCount)
        {
            return base.RepeatGrainReadWriteTransaction(grainStates, grainCount);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task MultiWriteToSingleGrainTransaction(string grainStates)
        {
            return base.MultiWriteToSingleGrainTransaction(grainStates);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task RWRWTest(string grainStates, int grainCount)
        {
            return base.RWRWTest(grainStates, grainCount);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task WRWRTest(string grainStates, int grainCount)
        {
            return base.WRWRTest(grainStates, grainCount);
        }

    }
}
