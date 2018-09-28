using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Transactions.Tests.DeactivatingInjection;
using Orleans.Transactions.Tests.DeactivationTransaction;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public class ControlledFaultInjectionTransactionTestRunner : TransactionTestRunnerBase
    {
        public ControlledFaultInjectionTransactionTestRunner(IGrainFactory grainFactory, ITestOutputHelper output)
         : base(grainFactory, output)
        { }

        [SkippableFact]
        public async Task SingleGrainReadTransaction()
        {
            const int expected = 5;

            IFaultInjectionTransactionTestGrain grain = grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid());
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
            IFaultInjectionTransactionTestGrain grain = this.grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid());
            int original = await grain.Get();
            await grain.Add(delta);
            await grain.Deactivate();
            int expected = original + delta;
            int actual = await grain.Get();
            Assert.Equal(expected, actual);
        }

        [SkippableTheory]
        [InlineData(TransactionFaultInjectPhase.AfterPrepare, FaultInjectionType.Deactivation)]
        [InlineData(TransactionFaultInjectPhase.AfterConfirm, FaultInjectionType.Deactivation)]
        [InlineData(TransactionFaultInjectPhase.AfterPrepared, FaultInjectionType.Deactivation)]
        [InlineData(TransactionFaultInjectPhase.AfterPrepareAndCommit, FaultInjectionType.Deactivation)]
        [InlineData(TransactionFaultInjectPhase.BeforePrepare, FaultInjectionType.ExceptionAfterStore)]
        [InlineData(TransactionFaultInjectPhase.BeforePrepare, FaultInjectionType.ExceptionBeforeStore)]
        [InlineData(TransactionFaultInjectPhase.BeforeConfirm, FaultInjectionType.ExceptionAfterStore)]
        [InlineData(TransactionFaultInjectPhase.BeforeConfirm, FaultInjectionType.ExceptionBeforeStore)]
        [InlineData(TransactionFaultInjectPhase.BeforePrepareAndCommit, FaultInjectionType.ExceptionAfterStore)]
        [InlineData(TransactionFaultInjectPhase.BeforePrepareAndCommit, FaultInjectionType.ExceptionBeforeStore)]
        public async Task MultiGrainWriteTransaction_FaultInjection(TransactionFaultInjectPhase injectionPhase, FaultInjectionType injectionType)
        {
            const int setval = 5;
            const int addval = 7;
            int expected = setval + addval;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;
            var faultInjectionControl = new FaultInjectionControl() { FaultInjectionPhase = injectionPhase, FaultInjectionType = injectionType };
            List<IFaultInjectionTransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => this.grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid()))
                    .ToList();

            IFaultInjectionTransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<IFaultInjectionTransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, setval);
            // add delay between transactions so confirmation errors don't bleed into neighboring transactions
            if (injectionPhase == TransactionFaultInjectPhase.BeforeConfirm || injectionPhase == TransactionFaultInjectPhase.AfterConfirm)
                await Task.Delay(TimeSpan.FromSeconds(30));
            try
            {
                await coordinator.MultiGrainAddAndFaultInjection(grains, addval, faultInjectionControl);
                // add delay between transactions so confirmation errors don't bleed into neighboring transactions
                if (injectionPhase == TransactionFaultInjectPhase.BeforeConfirm || injectionPhase == TransactionFaultInjectPhase.AfterConfirm)
                    await Task.Delay(TimeSpan.FromSeconds(30));
            }
            catch (OrleansTransactionAbortedException)
            {
                // add delay between transactions so errors don't bleed into neighboring transactions
                await coordinator.MultiGrainAddAndFaultInjection(grains, addval);
            }
            catch (OrleansTransactionException e)
            {
                this.output.WriteLine($"Call failed with exception: {e}, retrying without fault");
                bool cascadingAbort = false;
                bool firstAttempt = true;

                do
                {
                    cascadingAbort = false;
                    try
                    {
                        expected = await grains.First().Get() + addval;
                        await coordinator.MultiGrainAddAndFaultInjection(grains, addval);
                    }
                    catch (OrleansCascadingAbortException)
                    {
                        this.output.WriteLine($"Retry failed with OrleansCascadingAbortException: {e}, retrying without fault");
                        // should only encounter this when faulting after storage write
                        Assert.Equal(FaultInjectionType.ExceptionAfterStore, injectionType);
                        // only allow one retry
                        Assert.True(firstAttempt);
                        // add delay prevent castcading abort.
                        cascadingAbort = true;
                        firstAttempt = false;
                    }
                } while (cascadingAbort);
            }

            //if transactional state loaded correctly after reactivation, then following should pass
            foreach (var grain in grains)
            {
                int actual = await grain.Get();
                Assert.Equal(expected, actual);
            }
        }
    }
}
