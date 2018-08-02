using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Transactions.Tests.DeactivatingInjection;
using Orleans.Transactions.Tests.DeactivationTransaction;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public class GrainDeactivationTransactionTestRunner : TransactionTestRunnerBase
    {
        public GrainDeactivationTransactionTestRunner(IGrainFactory grainFactory, ITestOutputHelper output)
         : base(grainFactory, output)
        { }

        [SkippableFact]
        public async Task SingleGrainReadTransaction()
        {
            const int expected = 5;

            IDeactivatingTransactionTestGrain grain = grainFactory.GetGrain<IDeactivatingTransactionTestGrain>(Guid.NewGuid());
            await grain.Set(expected);
            int actual = await grain.Get();
            Assert.Equal(expected, actual);
            await grain.Deactivate();
            actual = await grain.Get();
            Assert.Equal(expected, actual);
        }

        [SkippableFact]
        public async Task SingleGrainWriteTransaction()
        {
            const int delta = 5;
            IDeactivatingTransactionTestGrain grain = this.grainFactory.GetGrain<IDeactivatingTransactionTestGrain>(Guid.NewGuid());
            int original = await grain.Get();
            await grain.Add(delta);
            await grain.Deactivate();
            int expected = original + delta;
            int actual = await grain.Get();
            Assert.Equal(expected, actual);
        }

        [SkippableFact]
        public async Task MultiGrainWriteTransaction_DeactivateAfterPerpare()
        {
            const int setval = 5;
            const int addval = 7;
            const int expected = setval + addval;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<IDeactivatingTransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => this.grainFactory.GetGrain<IDeactivatingTransactionTestGrain>(Guid.NewGuid()))
                    .ToList();

            IDeactivatingTransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<IDeactivatingTransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, setval);
            await coordinator.MultiGrainAddAndDeactivate(grains, addval, TransactionDeactivationPhase.AfterPrepare);

            foreach (var grain in grains)
            {
                int actual = await grain.Get();
                Assert.Equal(expected, actual);
            }
        }

        [SkippableFact]
        public async Task MultiGrainWriteTransaction_DeactivateAfterPrepareAndCommit()
        {
            const int setval = 5;
            const int addval = 7;
            const int expected = setval + addval;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<IDeactivatingTransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => this.grainFactory.GetGrain<IDeactivatingTransactionTestGrain>(Guid.NewGuid()))
                    .ToList();

            IDeactivatingTransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<IDeactivatingTransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, setval);
            await coordinator.MultiGrainAddAndDeactivate(grains, addval, TransactionDeactivationPhase.AfterPrepareAndCommit);

            foreach (var grain in grains)
            {
                int actual = await grain.Get();
                Assert.Equal(expected, actual);
            }
        }

        [SkippableFact]
        public async Task MultiGrainWriteTransaction_DeactivateAfterPrepared()
        {
            const int setval = 5;
            const int addval = 7;
            const int expected = setval + addval;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<IDeactivatingTransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => this.grainFactory.GetGrain<IDeactivatingTransactionTestGrain>(Guid.NewGuid()))
                    .ToList();

            IDeactivatingTransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<IDeactivatingTransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, setval);
            await coordinator.MultiGrainAddAndDeactivate(grains, addval, TransactionDeactivationPhase.AfterPrepared);

            foreach (var grain in grains)
            {
                int actual = await grain.Get();
                Assert.Equal(expected, actual);
            }
        }

        [SkippableFact]
        public async Task MultiGrainWriteTransaction_DeactivateAfterCommit()
        {
            const int setval = 5;
            const int addval = 7;
            const int expected = setval + addval;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<IDeactivatingTransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => this.grainFactory.GetGrain<IDeactivatingTransactionTestGrain>(Guid.NewGuid()))
                    .ToList();

            IDeactivatingTransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<IDeactivatingTransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, setval);
            await coordinator.MultiGrainAddAndDeactivate(grains, addval, TransactionDeactivationPhase.AfterConfirm);

            foreach (var grain in grains)
            {
                int actual = await grain.Get();
                Assert.Equal(expected, actual);
            }
        }
    }
}
