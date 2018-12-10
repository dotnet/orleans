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
        public override Task AbortTransactionOnExceptions(string grainStates)
        {
            return base.AbortTransactionOnExceptions(grainStates);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task MultiGrainAbortTransactionOnExceptions(string grainStates)
        {
            return base.MultiGrainAbortTransactionOnExceptions(grainStates);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionExceptionInnerExceptionOnlyContainsOneRootCauseException(string grainStates)
        {
            return base.AbortTransactionExceptionInnerExceptionOnlyContainsOneRootCauseException(grainStates);
        }

        [SkippableTheory()]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionOnOrphanCalls(string grainStates)
        {
            return base.AbortTransactionOnOrphanCalls(grainStates);
        }

    }
}
