using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class TocGoldenPathTestRunnerxUnit : TocGoldenPathTestRunner
    {
        protected TocGoldenPathTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        public override Task MultiGrainWriteTransaction(string grainStates, int grainCount)
        {
            return base.MultiGrainWriteTransaction(grainStates, grainCount);
        }
    }
}
