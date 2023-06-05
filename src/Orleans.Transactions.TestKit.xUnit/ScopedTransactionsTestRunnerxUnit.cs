using System.Threading.Tasks;

using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class ScopedTransactionsTestRunnerxUnit : ScopedTransactionsTestRunner
    {
        protected ScopedTransactionsTestRunnerxUnit(IGrainFactory grainFactory, ITransactionClient transactionFrame, ITestOutputHelper output)
        : base(grainFactory, transactionFrame, output.WriteLine) { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValue(string grainStates) => base.CreateTransactionScopeAndSetValue(grainStates);

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValueWithFailure(string grainStates) => base.CreateTransactionScopeAndSetValueWithFailure(grainStates);

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValueAndAssert(string grainStates) => base.CreateTransactionScopeAndSetValueAndAssert(grainStates);

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateNestedTransactionScopeAndSetValueAndInnerFailAndAssert(string grainStates) => base.CreateNestedTransactionScopeAndSetValueAndInnerFailAndAssert(grainStates);
    }
}
