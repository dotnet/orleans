
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;

namespace Orleans.Transactions.TestKit
{
    public abstract class TocFaultTransactionTestRunner : TransactionTestRunnerBase
    {
        protected TocFaultTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }

        public virtual async Task MultiGrainWriteTransactionWithCommitFailure(string grainStates, int grainCount)
        {
            const int expected = 5;

            ITransactionCommitterTestGrain committer = this.grainFactory.GetGrain<ITransactionCommitterTestGrain>(Guid.NewGuid());
            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(committer, new PassOperation("pass"), grains, expected);

            Func<Task> task = () => coordinator.MultiGrainAdd(committer, new FailOperation("fail"), grains, expected);
            await task.Should().ThrowAsync<OrleansTransactionAbortedException>();

            foreach (var grain in grains)
            {
                var actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    actual.Should().Be(expected);
                }
            }

            // TODO : Add verification that commit service receive call with proper args.
        }

        public virtual async Task MultiGrainWriteTransactionWithCommitException(string grainStates, int grainCount)
        {
            const int expected = 5;

            ITransactionCommitterTestGrain committer = this.grainFactory.GetGrain<ITransactionCommitterTestGrain>(Guid.NewGuid());
            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(committer, new PassOperation("pass"), grains, expected);

            Func<Task> task = () => coordinator.MultiGrainAdd(committer, new ThrowOperation("throw"), grains, expected);
            await task.Should().ThrowAsync<OrleansTransactionInDoubtException>();

            foreach (var grain in grains)
            {
                var actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    actual.Should().Be(expected);
                }
            }

            // TODO : Add verification that commit service receive call with proper args.
        }
    }
}
