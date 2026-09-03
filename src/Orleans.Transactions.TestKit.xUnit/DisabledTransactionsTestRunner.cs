using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    /// <inheritdoc cref="DisabledTransactionsTestRunner"/>
    public class DisabledTransactionsTestRunnerxUnit : DisabledTransactionsTestRunner
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DisabledTransactionsTestRunnerxUnit"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The xUnit test output helper.</param>
        public DisabledTransactionsTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
            : base(grainFactory, output.WriteLine) { }

        /// <inheritdoc cref="DisabledTransactionsTestRunner.TransactionGrainsThrowWhenTransactions(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.NoStateTransactionalGrain)]
        public override void TransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            base.TransactionGrainsThrowWhenTransactions(transactionTestGrainClassName);
        }

        /// <inheritdoc cref="DisabledTransactionsTestRunner.MultiTransactionGrainsThrowWhenTransactions(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.NoStateTransactionalGrain)]
        public override void MultiTransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            base.MultiTransactionGrainsThrowWhenTransactions(transactionTestGrainClassName);
        }
    }
}
