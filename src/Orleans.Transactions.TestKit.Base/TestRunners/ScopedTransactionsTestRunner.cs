using System;
using System.Threading.Tasks;

using FluentAssertions;

namespace Orleans.Transactions.TestKit
{
    public abstract class ScopedTransactionsTestRunner : TransactionTestRunnerBase
    {
        private readonly ITransactionClient _transactionClient;

        protected ScopedTransactionsTestRunner(IGrainFactory grainFactory, ITransactionClient transactionClient, Action<string> output)
            : base(grainFactory, output)
        {
            _transactionClient = transactionClient;
        }

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

        public virtual async Task CreateTransactionScopeAndSetValueWithFailure(string grainStates)
        {
            // Arrange
            var grain = RandomTestGrain(grainStates);

            // Act
            Func<Task> act = () => _transactionClient.RunTransaction(TransactionOption.Create, () => grain.SetAndThrow(57));

            // Assert
            await act.Should().ThrowAsync<OrleansTransactionAbortedException>(because: "Failure expected");
        }

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