using System;
using System.Threading.Tasks;

using AwesomeAssertions;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Runs transaction scenarios created through <see cref="ITransactionClient"/>.
    /// </summary>
    public abstract class ScopedTransactionsTestRunner : TransactionTestRunnerBase
    {
        private readonly ITransactionClient _transactionClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="ScopedTransactionsTestRunner"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="transactionClient">The client used to create transaction scopes.</param>
        /// <param name="output">The callback used to write test output.</param>
        protected ScopedTransactionsTestRunner(IGrainFactory grainFactory, ITransactionClient transactionClient, Action<string> output)
            : base(grainFactory, output)
        {
            _transactionClient = transactionClient;
        }

        /// <summary>
        /// Verifies that a grain value can be set within a newly created transaction scope.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select the test grain.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task CreateTransactionScopeAndSetValue(string grainStates)
        {
            // Arrange
            var grain = RandomTestGrain(grainStates);

            // Act
            Func<Task> act = () => grain.Set(57);

            await _transactionClient.RunTransaction(TransactionOption.Create, async () =>
                // Assert
                await act.Should().NotThrowAsync(because: "No failure expected"));
        }

        /// <summary>
        /// Verifies that a failure within a newly created transaction scope aborts the transaction.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select the test grain.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task CreateTransactionScopeAndSetValueWithFailure(string grainStates)
        {
            // Arrange
            var grain = RandomTestGrain(grainStates);

            // Act
            Func<Task> act = () => _transactionClient.RunTransaction(TransactionOption.Create, () => grain.SetAndThrow(57));

            // Assert
            await act.Should().ThrowAsync<OrleansTransactionAbortedException>(because: "Failure expected");
        }

        /// <summary>
        /// Sets and reads a grain value within one transaction scope and verifies the transactional result.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select the test grain.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task CreateTransactionScopeAndSetValueAndAssert(string grainStates)
        {
            var result = Array.Empty<int>();

            // Arrange
            var grain = RandomTestGrain(grainStates);

            // Act
            await _transactionClient.RunTransaction(TransactionOption.Create, async () =>
            {
                await grain.Set(57);
                result = await grain.Get();
            });

            // Assert
            result.Should().OnlyContain(number => number == 57);
        }

        /// <summary>
        /// Verifies that an aborted nested transaction does not prevent the outer transaction from committing its value.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select the test grain.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task CreateNestedTransactionScopeAndSetValueAndInnerFailAndAssert(string grainStates)
        {
            var result = Array.Empty<int>();

            // Arrange
            var grain = RandomTestGrain(grainStates);

            // Act
            await _transactionClient.RunTransaction(TransactionOption.Create, async () =>
            {
                try
                {
                    await _transactionClient.RunTransaction(TransactionOption.Create, async () => await grain.SetAndThrow(67));
                }
                catch
                { }

                await grain.Set(57);
            });

            result = await grain.Get();

            // Assert
            result.Should().OnlyContain(number => number == 57);
        }
    }
}