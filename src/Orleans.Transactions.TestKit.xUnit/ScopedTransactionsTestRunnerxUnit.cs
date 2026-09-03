using System.Threading.Tasks;

using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    /// <inheritdoc cref="ScopedTransactionsTestRunner"/>
    public abstract class ScopedTransactionsTestRunnerxUnit : ScopedTransactionsTestRunner
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ScopedTransactionsTestRunnerxUnit"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="transactionFrame">The client used to create transaction scopes.</param>
        /// <param name="output">The xUnit test output helper.</param>
        protected ScopedTransactionsTestRunnerxUnit(IGrainFactory grainFactory, ITransactionClient transactionFrame, ITestOutputHelper output)
        : base(grainFactory, transactionFrame, output.WriteLine) { }

        /// <inheritdoc cref="ScopedTransactionsTestRunner.CreateTransactionScopeAndSetValue(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValue(string grainStates)
        {
            return base.CreateTransactionScopeAndSetValue(grainStates);
        }

        /// <inheritdoc cref="ScopedTransactionsTestRunner.CreateTransactionScopeAndSetValueWithFailure(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValueWithFailure(string grainStates)
        {
            return base.CreateTransactionScopeAndSetValueWithFailure(grainStates);
        }

        /// <inheritdoc cref="ScopedTransactionsTestRunner.CreateTransactionScopeAndSetValueAndAssert(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task CreateTransactionScopeAndSetValueAndAssert(string grainStates)
        {
            return base.CreateTransactionScopeAndSetValueAndAssert(grainStates);
        }

        /// <inheritdoc cref="ScopedTransactionsTestRunner.CreateNestedTransactionScopeAndSetValueAndInnerFailAndAssert(string)"/>
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
