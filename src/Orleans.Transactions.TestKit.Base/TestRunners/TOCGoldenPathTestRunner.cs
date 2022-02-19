
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;

namespace Orleans.Transactions.TestKit
{
    public abstract class TocGoldenPathTestRunner : TransactionTestRunnerBase
    {
        protected TocGoldenPathTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }
        public virtual async Task MultiGrainWriteTransaction(string grainStates, int grainCount)
        {
            const int expected = 5;

            ITransactionCommitterTestGrain committer = this.grainFactory.GetGrain<ITransactionCommitterTestGrain>(Guid.NewGuid());
            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(committer, new PassOperation("pass"), grains, expected);

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
