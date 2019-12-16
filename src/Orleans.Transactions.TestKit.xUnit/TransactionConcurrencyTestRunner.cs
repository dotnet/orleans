using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class TransactionConcurrencyTestRunnerxUnit : TransactionConcurrencyTestRunner
    {
        protected TransactionConcurrencyTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        /// <summary>
        /// Two transaction share a single grain
        /// </summary>
        /// <param name="grainStates"></param>
        /// <returns></returns>
        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task SingleSharedGrainTest(string grainStates)
        {
            return base.SingleSharedGrainTest(grainStates);
        }

        /// <summary>
        /// Chain of transactions, each dependent on the results of the previous
        /// </summary>
        /// <param name="grainStates"></param>
        /// <returns></returns>
        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task TransactionChainTest(string grainStates)
        {
            return base.TransactionChainTest(grainStates);
        }

        /// <summary>
        /// Single transaction containing two grains is dependent on two other transaction, one from each grain
        /// </summary>
        /// <param name="grainStates"></param>
        /// <returns></returns>
        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public override Task TransactionTreeTest(string grainStates)
        {
            return base.TransactionTreeTest(grainStates);
        }
    }
}
