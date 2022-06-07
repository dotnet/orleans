using System;
using System.Threading.Tasks;

using FluentAssertions;

namespace Orleans.Transactions.TestKit
{
    public abstract class FramedTransactionsTestRunner : TransactionTestRunnerBase
    {
        private readonly ITransactionFrame _transactionFrame;

        protected FramedTransactionsTestRunner(IGrainFactory grainFactory, ITransactionFrame transactionFrame, Action<string> output)
            : base(grainFactory, output)
        {
            _transactionFrame = transactionFrame;
        }

        public virtual async Task CreateTransactionScopeAndSetValue(string grainStates)
        {
            // Arrange
            var grain = RandomTestGrain(grainStates);

            // Act
            Func<Task> act = () => grain.Set(57);

            await _transactionFrame.RunScope(TransactionOption.Create, async () =>
                // Assert
                await act.Should().NotThrowAsync(because: "No failure expected"));
        }

        public virtual async Task CreateTransactionScopeAndSetValueWithFailure(string grainStates)
        {
            // Arrange
            var grain = RandomTestGrain(grainStates);

            // Act
            Func<Task> act = () => grain.SetAndThrow(57);

            await _transactionFrame.RunScope(TransactionOption.Create, async () =>
                // Assert
                await act.Should().ThrowAsync<OrleansTransactionAbortedException>(because: "Failure expected"));
        }

        public virtual async Task CreateTransactionScopeAndSetValueAndAssert(string grainStates)
        {
            var result = Array.Empty<int>();

            // Arrange
            var grain = RandomTestGrain(grainStates);

            // Act
            await _transactionFrame.RunScope(TransactionOption.Create, async () =>
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
            await _transactionFrame.RunScope(TransactionOption.Create, async () =>
            {
                await grain.Set(57);

                try
                {
                    await _transactionFrame.RunScope(TransactionOption.Create, async () => await grain.SetAndThrow(67));
                }
                catch
                { }

                result = await grain.Get();
            });

            // Assert
            result.Should().OnlyContain(number => number == 57);
        }
    }
}
