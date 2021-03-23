using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public class ControlledFaultInjectionTransactionTestRunnerxUnit : ControlledFaultInjectionTransactionTestRunner
    {
        public ControlledFaultInjectionTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
         : base(grainFactory, output.WriteLine)
        { }

        [SkippableFact]
        public override Task SingleGrainReadTransaction()
        {
            return base.SingleGrainReadTransaction();
        }

        [SkippableFact]
        public override Task SingleGrainWriteTransaction()
        {
            return base.SingleGrainWriteTransaction();
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
        public override Task MultiGrainWriteTransaction_FaultInjection(TransactionFaultInjectPhase injectionPhase, FaultInjectionType injectionType)
        {
            return base.MultiGrainWriteTransaction_FaultInjection(injectionPhase, injectionType);
        }
    }
}
