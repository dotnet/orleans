using System;
using System.Threading.Tasks;
using Orleans.Transactions.TestKit.Base;
using Orleans.Transactions.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class TransactionalStateStorageTestRunnerxUnit<TState> : TransactionalStateStorageTestRunner<TState>
        where TState: class, ITestState, new()
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="stateStorageFactory">factory to create ITransactionalStateStorage, the test runner are assuming the state 
        /// in storage is empty when ITransactionalStateStorage was created </param>
        /// <param name="stateFactory">factory to create TState for test</param>
        /// <param name="grainFactory">grain Factory needed for test runner</param>
        /// <param name="testOutput">test output to helpful messages</param>
        public TransactionalStateStorageTestRunnerxUnit(Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory,
            Func<TState> stateFactory, IGrainFactory grainFactory, ITestOutputHelper testOutput)
            : base(stateStorageFactory, stateFactory, grainFactory, testOutput.WriteLine)
        {
        }

        [Fact]
        public override Task FirstTime_Load_ShouldReturnEmptyLoadResponse()
        {
            return base.FirstTime_Load_ShouldReturnEmptyLoadResponse();
        }

        [Fact]
        public override Task RunAll()
        {
            return base.RunAll();
        }

    }
}
