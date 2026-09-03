using System.Threading.Tasks;
using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    /// <inheritdoc cref="GrainFaultTransactionTestRunner"/>
    public class GrainFaultTransactionTestRunnerxUnit : GrainFaultTransactionTestRunner
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GrainFaultTransactionTestRunnerxUnit"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The xUnit test output helper.</param>
        public GrainFaultTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine)
        { }

        /// <inheritdoc cref="GrainFaultTransactionTestRunner.AbortTransactionOnExceptions(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionOnExceptions(string grainStates)
        {
            return base.AbortTransactionOnExceptions(grainStates);
        }

        /// <inheritdoc cref="GrainFaultTransactionTestRunner.AbortTransactionOnReadOnlyViolatedException(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionOnReadOnlyViolatedException(string grainStates)
        {
            return base.AbortTransactionOnReadOnlyViolatedException(grainStates);
        }

        /// <inheritdoc cref="GrainFaultTransactionTestRunner.MultiGrainAbortTransactionOnExceptions(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task MultiGrainAbortTransactionOnExceptions(string grainStates)
        {
            return base.MultiGrainAbortTransactionOnExceptions(grainStates);
        }

        /// <inheritdoc cref="GrainFaultTransactionTestRunner.AbortTransactionExceptionInnerExceptionOnlyContainsOneRootCauseException(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task AbortTransactionExceptionInnerExceptionOnlyContainsOneRootCauseException(string grainStates)
        {
            return base.AbortTransactionExceptionInnerExceptionOnlyContainsOneRootCauseException(grainStates);
        }

        /// <inheritdoc cref="GrainFaultTransactionTestRunner.AbortTransactionOnOrphanCalls(string)"/>
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
