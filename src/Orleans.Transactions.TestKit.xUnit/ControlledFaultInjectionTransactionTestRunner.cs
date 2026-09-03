using System.Threading.Tasks;
using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    /// <inheritdoc cref="ControlledFaultInjectionTransactionTestRunner"/>
    public class ControlledFaultInjectionTransactionTestRunnerxUnit : ControlledFaultInjectionTransactionTestRunner
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ControlledFaultInjectionTransactionTestRunnerxUnit"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The xUnit test output helper.</param>
        public ControlledFaultInjectionTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
         : base(grainFactory, output.WriteLine)
        { }

        /// <inheritdoc cref="ControlledFaultInjectionTransactionTestRunner.SingleGrainReadTransaction"/>
        [Fact]
        public override Task SingleGrainReadTransaction()
        {
            return base.SingleGrainReadTransaction();
        }

        /// <inheritdoc cref="ControlledFaultInjectionTransactionTestRunner.SingleGrainWriteTransaction"/>
        [Fact]
        public override Task SingleGrainWriteTransaction()
        {
            return base.SingleGrainWriteTransaction();
        }

        /// <inheritdoc cref="ControlledFaultInjectionTransactionTestRunner.MultiGrainWriteTransaction_FaultInjection(TransactionFaultInjectPhase, FaultInjectionType)"/>
        [Theory]
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
