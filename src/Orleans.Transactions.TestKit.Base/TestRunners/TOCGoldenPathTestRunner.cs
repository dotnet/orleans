
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Runs successful transaction commit service scenarios.
    /// </summary>
    public abstract class TocGoldenPathTestRunner : TransactionTestRunnerBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TocGoldenPathTestRunner"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The callback used to write test output.</param>
        protected TocGoldenPathTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }

        /// <summary>
        /// Verifies a successful multi-grain write coordinated with a transaction commit service.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <param name="grainCount">The number of grains participating in the transaction.</param>
        /// <returns>A task which represents the test.</returns>
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
