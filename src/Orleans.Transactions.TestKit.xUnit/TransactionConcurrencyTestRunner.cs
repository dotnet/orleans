using System.Threading.Tasks;
using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    /// <inheritdoc cref="TransactionConcurrencyTestRunner"/>
    public abstract class TransactionConcurrencyTestRunnerxUnit : TransactionConcurrencyTestRunner
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionConcurrencyTestRunnerxUnit"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The xUnit test output helper.</param>
        protected TransactionConcurrencyTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        /// <inheritdoc cref="TransactionConcurrencyTestRunner.SingleSharedGrainTest(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task SingleSharedGrainTest(string grainStates)
        {
            return base.SingleSharedGrainTest(grainStates);
        }

        /// <inheritdoc cref="TransactionConcurrencyTestRunner.TransactionChainTest(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task TransactionChainTest(string grainStates)
        {
            return base.TransactionChainTest(grainStates);
        }

        /// <inheritdoc cref="TransactionConcurrencyTestRunner.TransactionTreeTest(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task TransactionTreeTest(string grainStates)
        {
            return base.TransactionTreeTest(grainStates);
        }
    }
}
