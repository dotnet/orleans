using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public class GrainFaultTransactionTestRunnerxUnit : GrainFaultTransactionTestRunner
    {
        public GrainFaultTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine)
        { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionOnExceptions(string grainStates) => base.AbortTransactionOnExceptions(grainStates);

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task MultiGrainAbortTransactionOnExceptions(string grainStates) => base.MultiGrainAbortTransactionOnExceptions(grainStates);

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionExceptionInnerExceptionOnlyContainsOneRootCauseException(string grainStates) => base.AbortTransactionExceptionInnerExceptionOnlyContainsOneRootCauseException(grainStates);

        [SkippableTheory()]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionOnOrphanCalls(string grainStates) => base.AbortTransactionOnOrphanCalls(grainStates);

    }
}
