using System;
using System.Threading.Tasks;
using FluentAssertions.Equivalency;
using Orleans.Transactions.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class TransactionalStateStorageTestRunnerxUnit<TState> : TransactionalStateStorageTestRunner<TState>
        where TState: class, new()
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="stateStorageFactory">factory to create ITransactionalStateStorage, the test runner are assuming the state 
        /// in storage is empty when ITransactionalStateStorage was created </param>
        /// <param name="stateFactory">factory to create TState for test</param>
        /// <param name="grainFactory">grain Factory needed for test runner</param>
        /// <param name="testOutput">test output to helpful messages</param>
        /// <param name="assertConfig">A reference to the FluentAssertions.Equivalency.EquivalencyAssertionOptions`1
        ///     configuration object that can be used to influence the way the object graphs
        ///     are compared</param>
        public TransactionalStateStorageTestRunnerxUnit(Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory,
            Func<int, TState> stateFactory, IGrainFactory grainFactory, ITestOutputHelper testOutput,
            Func<EquivalencyAssertionOptions<TState>, EquivalencyAssertionOptions<TState>> assertConfig = null)
            : base(stateStorageFactory, stateFactory, grainFactory, testOutput.WriteLine, assertConfig)
        {
        }

        [Fact]
        public override Task FirstTime_Load_ShouldReturnEmptyLoadResponse()
        {
            return base.FirstTime_Load_ShouldReturnEmptyLoadResponse();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public override Task ConfirmOne(bool useTwoSteps)
        {
            return base.ConfirmOne(useTwoSteps);
        }

        [Fact]
        public override Task CancelOne()
        {
            return base.CancelOne();
        }

        [Fact]
        public override Task ReplaceOne()
        {
            return base.ReplaceOne();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        [InlineData(true, false)]
        public override Task ConfirmOneAndCancelOne(bool useTwoSteps, bool reverseOrder)
        {
            return base.ConfirmOneAndCancelOne(useTwoSteps, reverseOrder);
        }

        [Fact]
        public override Task GrowingBatch()
        {
            return base.GrowingBatch();
        }

        [Fact]
        public override Task ShrinkingBatch()
        {
            return base.ShrinkingBatch();
        }

        [Theory]
        [InlineData(99)]
        [InlineData(100)]
        [InlineData(200)]
        public override Task PrepareMany(int count)
        {
            return base.PrepareMany(count);
        }

        [Theory]
        [InlineData(99, true)]
        [InlineData(99, false)]
        [InlineData(100, true)]
        [InlineData(100, false)]
        [InlineData(200, true)]
        [InlineData(200, false)]
        public override Task ConfirmMany(int count, bool useTwoSteps)
        {
            return base.ConfirmMany(count, useTwoSteps);
        }

        [Theory]
        [InlineData(99)]
        [InlineData(100)]
        [InlineData(200)]
        public override Task CancelMany(int count)
        {
            return base.CancelMany(count);
        }

        [Theory]
        [InlineData(99)]
        [InlineData(100)]
        [InlineData(200)]
        public override Task ReplaceMany(int count)
        {
            return base.ReplaceMany(count);
        }
    }
}
