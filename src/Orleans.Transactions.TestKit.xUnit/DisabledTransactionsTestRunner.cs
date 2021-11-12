using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public class DisabledTransactionsTestRunnerxUnit : DisabledTransactionsTestRunner
    {
        protected DisabledTransactionsTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.NoStateTransactionalGrain)]
        public override async Task TransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
             await base.TransactionGrainsThrowWhenTransactions(transactionTestGrainClassName);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.NoStateTransactionalGrain)]
        public override async Task MultiTransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            await base.MultiTransactionGrainsThrowWhenTransactions(transactionTestGrainClassName);
        }
    }
}
