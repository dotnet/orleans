using System.Threading.Tasks;
using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    /// <inheritdoc cref="TocGoldenPathTestRunner"/>
    public abstract class TocGoldenPathTestRunnerxUnit : TocGoldenPathTestRunner
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TocGoldenPathTestRunnerxUnit"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The xUnit test output helper.</param>
        protected TocGoldenPathTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        /// <inheritdoc cref="TocGoldenPathTestRunner.MultiGrainWriteTransaction(string, int)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        public override Task MultiGrainWriteTransaction(string grainStates, int grainCount)
        {
            return base.MultiGrainWriteTransaction(grainStates, grainCount);
        }
    }
}
