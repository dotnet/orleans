using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;

namespace Orleans.Transactions.TestKit
{
    public class ControlledFaultInjectionTransactionTestRunner : TransactionTestRunnerBase
    {
        public ControlledFaultInjectionTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
         : base(grainFactory, output)
        { }
        
        public virtual async Task SingleGrainReadTransaction()
        {
            const int expected = 5;

            var grain = grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid());
            await grain.Set(expected);
            var actual = await grain.Get();
            actual.Should().Be(expected);
            await grain.Deactivate();
            actual = await grain.Get();
            actual.Should().Be(expected);
        }
        
        public virtual async Task SingleGrainWriteTransaction()
        {
            const int delta = 5;
            var grain = this.grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid());
            var original = await grain.Get();
            await grain.Add(delta);
            await grain.Deactivate();
            var expected = original + delta;
            var actual = await grain.Get();
            actual.Should().Be(expected);
        }

        public virtual async Task MultiGrainWriteTransaction_FaultInjection(TransactionFaultInjectPhase injectionPhase, FaultInjectionType injectionType)
        {
            const int setval = 5;
            const int addval = 7;
            var expected = setval + addval;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;
            var faultInjectionControl = new FaultInjectionControl() { FaultInjectionPhase = injectionPhase, FaultInjectionType = injectionType };
            var grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => this.grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid()))
                    .ToList();

            var coordinator = this.grainFactory.GetGrain<IFaultInjectionTransactionCoordinatorGrain>(Guid.NewGuid());

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
                this.testOutput($"Call failed with exception: {e}, retrying without fault");
                var cascadingAbort = false;
                var firstAttempt = true;

                do
                {
                    cascadingAbort = false;
                    try
                    {
                        expected = await grains[0].Get() + addval;
                        await coordinator.MultiGrainAddAndFaultInjection(grains, addval);
                    }
                    catch (OrleansCascadingAbortException)
                    {
                        this.testOutput($"Retry failed with OrleansCascadingAbortException: {e}, retrying without fault");
                        // should only encounter this when faulting after storage write
                        injectionType.Should().Be(FaultInjectionType.ExceptionAfterStore);
                        // only allow one retry
                        firstAttempt.Should().BeTrue();
                        // add delay prevent castcading abort.
                        cascadingAbort = true;
                        firstAttempt = false;
                    }
                } while (cascadingAbort);
            }

            //if transactional state loaded correctly after reactivation, then following should pass
            foreach (var grain in grains)
            {
                var actual = await grain.Get();
                actual.Should().Be(expected);
            }
        }
    }
}
