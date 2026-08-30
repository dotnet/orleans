using System.Threading.Tasks;
using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class TocGoldenPathTestRunnerxUnit : TocGoldenPathTestRunner
    {
        protected TocGoldenPathTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        public override Task MultiGrainWriteTransaction(string grainStates, int grainCount)
        {
            return base.MultiGrainWriteTransaction(grainStates, grainCount);
        }
    }
}
