using System.Threading.Tasks;
using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    /// <inheritdoc cref="GoldenPathTransactionTestRunner"/>
    public abstract class GoldenPathTransactionTestRunnerxUnit : GoldenPathTransactionTestRunner
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GoldenPathTransactionTestRunnerxUnit"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The xUnit test output helper.</param>
        protected GoldenPathTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        /// <inheritdoc cref="GoldenPathTransactionTestRunner.SingleGrainReadTransaction(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task SingleGrainReadTransaction(string grainStates)
        {
            return base.SingleGrainReadTransaction(grainStates);
        }

        /// <inheritdoc cref="GoldenPathTransactionTestRunner.SingleGrainWriteTransaction(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task SingleGrainWriteTransaction(string grainStates)
        {
            return base.SingleGrainWriteTransaction(grainStates);
        }

        /// <inheritdoc cref="GoldenPathTransactionTestRunner.MultiGrainWriteTransaction(string, int)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task MultiGrainWriteTransaction(string grainStates, int grainCount)
        {
            return base.MultiGrainWriteTransaction(grainStates, grainCount);
        }

        /// <inheritdoc cref="GoldenPathTransactionTestRunner.MultiGrainReadWriteTransaction(string, int)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task MultiGrainReadWriteTransaction(string grainStates, int grainCount)
        {
            return base.MultiGrainReadWriteTransaction(grainStates, grainCount);
        }

        /// <inheritdoc cref="GoldenPathTransactionTestRunner.RepeatGrainReadWriteTransaction(string, int)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task RepeatGrainReadWriteTransaction(string grainStates, int grainCount)
        {
            return base.RepeatGrainReadWriteTransaction(grainStates, grainCount);
        }

        /// <inheritdoc cref="GoldenPathTransactionTestRunner.MultiWriteToSingleGrainTransaction(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task MultiWriteToSingleGrainTransaction(string grainStates)
        {
            return base.MultiWriteToSingleGrainTransaction(grainStates);
        }

        /// <inheritdoc cref="GoldenPathTransactionTestRunner.RWRWTest(string, int)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task RWRWTest(string grainStates, int grainCount)
        {
            return base.RWRWTest(grainStates, grainCount);
        }

        /// <inheritdoc cref="GoldenPathTransactionTestRunner.WRWRTest(string, int)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task WRWRTest(string grainStates, int grainCount)
        {
            return base.WRWRTest(grainStates, grainCount);
        }

    }
}
