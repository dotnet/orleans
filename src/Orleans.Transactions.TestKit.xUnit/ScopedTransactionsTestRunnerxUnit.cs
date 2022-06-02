using System.Threading.Tasks;

using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class ScopedTransactionsTestRunnerxUnit : ScopedTransactionsTestRunner
    {
        protected ScopedTransactionsTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValueWithTransactionAttribute(string grainStates)
        {
            return base.CreateTransactionScopeAndSetValueWithTransactionAttribute(grainStates);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValueWithoutTransactionAttribute(string grainStates)
        {
            return base.CreateTransactionScopeAndSetValueWithoutTransactionAttribute(grainStates);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValueAndFailWithTransactionAttribute(string grainStates)
        {
            return base.CreateTransactionScopeAndSetValueAndFailWithTransactionAttribute(grainStates);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValueAndFailWithoutTransactionAttribute(string grainStates)
        {
            return base.CreateTransactionScopeAndSetValueAndFailWithoutTransactionAttribute(grainStates);
        }
    }
}
