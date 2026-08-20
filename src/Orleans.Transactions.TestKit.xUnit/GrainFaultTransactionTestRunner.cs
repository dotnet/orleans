using System.Threading.Tasks;
using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    public class GrainFaultTransactionTestRunnerxUnit : GrainFaultTransactionTestRunner
    {
        public GrainFaultTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine)
        { }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionOnExceptions(string grainStates)
        {
            return base.AbortTransactionOnExceptions(grainStates);
        }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionOnReadOnlyViolatedException(string grainStates)
        {
            return base.AbortTransactionOnReadOnlyViolatedException(grainStates);
        }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task MultiGrainAbortTransactionOnExceptions(string grainStates)
        {
            return base.MultiGrainAbortTransactionOnExceptions(grainStates);
        }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionExceptionInnerExceptionOnlyContainsOneRootCauseException(string grainStates)
        {
            return base.AbortTransactionExceptionInnerExceptionOnlyContainsOneRootCauseException(grainStates);
        }

        [Theory()]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionOnOrphanCalls(string grainStates)
        {
            return base.AbortTransactionOnOrphanCalls(grainStates);
        }

    }
}
