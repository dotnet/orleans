using System;
using System.Threading.Tasks;

using FluentAssertions;

namespace Orleans.Transactions.TestKit
{
    public abstract class ScopedTransactionsTestRunner : TransactionTestRunnerBase
    {
        protected ScopedTransactionsTestRunner(IGrainFactory grainFactory, Action<string> output)
            : base(grainFactory, output) { }

        public virtual async Task CreateTransactionScopeAndSetValueWithTransactionAttribute(string grainStates)
        {
            // Arrange
            ITransactionTestGrain grain = RandomTestGrain(grainStates);

            // Act
            Func<Task> act = () => grain.CreateScopeAndSetValueWithAmbientTransaction(57);

            // Assert
            await act.Should().NotThrowAsync(because: "No failure expected");
        }

        public virtual async Task CreateTransactionScopeAndSetValueWithoutTransactionAttribute(string grainStates)
        {
            // Arrange
            ITransactionTestGrain grain = RandomTestGrain(grainStates);

            // Act
            Func<Task> act = () => grain.CreateScopeAndSetValueWithoutAmbientTransaction(57);

            // Assert
            await act.Should().NotThrowAsync(because: "No failure expected");
        }

        public virtual async Task CreateTransactionScopeAndSetValueAndFailWithTransactionAttribute(string grainStates)
        {
            // Arrange
            ITransactionTestGrain grain = RandomTestGrain(grainStates);

            // Act
            Func<Task> act = () => grain.CreateScopeAndSetValueAndFailWithAmbientTransaction(57);

            // Assert
            await act.Should().ThrowAsync<OrleansTransactionAbortedException>(because: "Failure expected");
        }

        public virtual async Task CreateTransactionScopeAndSetValueAndFailWithoutTransactionAttribute(string grainStates)
        {
            // Arrange
            ITransactionTestGrain grain = RandomTestGrain(grainStates);

            // Act
            Func<Task> act = () => grain.CreateScopeAndSetValueAndFailWithoutAmbientTransaction(57);

            // Assert
            await act.Should().ThrowAsync<OrleansTransactionAbortedException>(because: "Failure expected");
        }
    }
}
