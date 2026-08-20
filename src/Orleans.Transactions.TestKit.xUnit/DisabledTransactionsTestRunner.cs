using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    public class DisabledTransactionsTestRunnerxUnit : DisabledTransactionsTestRunner
    {
        protected DisabledTransactionsTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        [Theory]
        [InlineData(TransactionTestConstants.NoStateTransactionalGrain)]
        public override void TransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
             base.TransactionGrainsThrowWhenTransactions(transactionTestGrainClassName);
        }

        [Theory]
        [InlineData(TransactionTestConstants.NoStateTransactionalGrain)]
        public override void MultiTransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            base.MultiTransactionGrainsThrowWhenTransactions(transactionTestGrainClassName);
        }
    }
}
