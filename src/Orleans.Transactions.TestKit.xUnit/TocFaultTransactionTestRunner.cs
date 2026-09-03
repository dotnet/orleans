using System.Threading.Tasks;
using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    /// <inheritdoc cref="TocFaultTransactionTestRunner"/>
    public abstract class TocFaultTransactionTestRunnerxUnit : TocFaultTransactionTestRunner
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TocFaultTransactionTestRunnerxUnit"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The xUnit test output helper.</param>
        protected TocFaultTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        /// <inheritdoc cref="TocFaultTransactionTestRunner.MultiGrainWriteTransactionWithCommitFailure(string, int)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task MultiGrainWriteTransactionWithCommitFailure(string grainStates, int grainCount)
        {
            return base.MultiGrainWriteTransactionWithCommitFailure(grainStates, grainCount);
        }

        /// <inheritdoc cref="TocFaultTransactionTestRunner.MultiGrainWriteTransactionWithCommitException(string, int)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public override Task MultiGrainWriteTransactionWithCommitException(string grainStates, int grainCount)
        {
            return base.MultiGrainWriteTransactionWithCommitException(grainStates, grainCount);
        }
    }
}
