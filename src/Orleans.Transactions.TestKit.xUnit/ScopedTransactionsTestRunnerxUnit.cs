using System.Threading.Tasks;

using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class ScopedTransactionsTestRunnerxUnit : ScopedTransactionsTestRunner
    {
        protected ScopedTransactionsTestRunnerxUnit(IGrainFactory grainFactory, ITransactionClient transactionFrame, ITestOutputHelper output)
        : base(grainFactory, transactionFrame, output.WriteLine) { }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValue(string grainStates)
        {
            return base.CreateTransactionScopeAndSetValue(grainStates);
        }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValueWithFailure(string grainStates)
        {
            return base.CreateTransactionScopeAndSetValueWithFailure(grainStates);
        }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValueAndAssert(string grainStates)
        {
            return base.CreateTransactionScopeAndSetValueAndAssert(grainStates);
        }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateNestedTransactionScopeAndSetValueAndInnerFailAndAssert(string grainStates)
        {
            return base.CreateNestedTransactionScopeAndSetValueAndInnerFailAndAssert(grainStates);
        }
    }
}
