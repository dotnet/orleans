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
    public class ControlledFaultInjectionTransactionTestRunner : TransactionTestRunnerBase
    {
        public ControlledFaultInjectionTransactionTestRunner(IGrainFactory grainFactory, ITestOutputHelper output)
         : base(grainFactory, output)
        { }

        [SkippableTheory]
        [InlineData(TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain)]
        public async Task SingleGrainReadTransaction(string grainClassName)
        {
            const int expected = 5;

            IFaultInjectionTransactionTestGrain grain = grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid(), grainClassName);
            await grain.Set(expected);
            List<int> actuals= await grain.Get();
            actuals.ForEach(actual => Assert.Equal(expected, actual));
            await grain.Deactivate();
            actuals = await grain.Get();
            actuals.ForEach(actual => Assert.Equal(expected, actual));
        }

        [SkippableTheory]
        [InlineData(TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain)]
        public async Task SingleGrainWriteTransaction(string grainClassName)
        {
            const int delta = 5;
            IFaultInjectionTransactionTestGrain grain = this.grainFactory.GetGrain<IFaultInjectionTransactionTestGrain>(Guid.NewGuid(), grainClassName);
            List<int> originals = await grain.Get();
            await grain.Add(delta);
            await grain.Deactivate();
            int expected = originals[0] + delta;
            List<int> actuals = await grain.Get();
            actuals.ForEach(actual => Assert.Equal(expected, actual));
        }

        [SkippableTheory]
        [InlineData(TransactionFaultInjectPhase.AfterPrepare, FaultInjectionType.Deactivation, 
            TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain,
            TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionFaultInjectPhase.AfterConfirm, FaultInjectionType.Deactivation,
            TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain,
            TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionFaultInjectPhase.AfterPrepared, FaultInjectionType.Deactivation,
            TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain,
            TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionFaultInjectPhase.AfterPrepareAndCommit, FaultInjectionType.Deactivation,
            TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain,
            TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionFaultInjectPhase.BeforePrepare, FaultInjectionType.ExceptionAfterStore,
            TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain,
            TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionFaultInjectPhase.BeforePrepare, FaultInjectionType.ExceptionBeforeStore,
            TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain,
            TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionFaultInjectPhase.BeforeConfirm, FaultInjectionType.ExceptionAfterStore,
            TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain,
            TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionFaultInjectPhase.BeforeConfirm, FaultInjectionType.ExceptionBeforeStore,
            TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain,
            TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionFaultInjectPhase.BeforePrepareAndCommit, FaultInjectionType.ExceptionAfterStore,
            TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain,
            TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionFaultInjectPhase.BeforePrepareAndCommit, FaultInjectionType.ExceptionBeforeStore,
            TransactionFaultInjectionGrainNames.SingleStateFaultInjectionTransactionalGrain,
            TransactionTestConstants.MaxCoordinatedTransactions)]
        public async Task MultiGrainWriteTransaction_FaultInjection(TransactionFaultInjectPhase injectionPhase, FaultInjectionType injectionType, 
            string grainClassName, int grainCount)
        {
            const int setval = 5;
            const int addval = 7;
            const int expected = setval + addval;
            var faultInjectionControl = new FaultInjectionControl(){FaultInjectionPhase = injectionPhase, FaultInjectionType = injectionType};
            List<IFaultInjectionTransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain<IFaultInjectionTransactionTestGrain>(grainClassName))
                    .ToList();

            IFaultInjectionTransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<IFaultInjectionTransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, setval);
            try
            {
                await coordinator.MultiGrainAddAndFaultInjection(grains, addval, faultInjectionControl);
            }
            catch (OrleansTransactionException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                //if failed due to timeout or other legitimate transaction exception, try again. This should succeed 
                await coordinator.MultiGrainAddAndFaultInjection(grains, addval);
            }

            //if transactional state loaded correctly after reactivation, then following should pass
            foreach (var grain in grains)
            {
                List<int> actuals = await grain.Get();
                actuals.ForEach(actual => Assert.Equal(expected, actual));
            }
        }
    }
}
