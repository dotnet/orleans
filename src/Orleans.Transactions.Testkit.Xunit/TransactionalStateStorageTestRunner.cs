using System;
using System.Threading.Tasks;
using Orleans.Transactions.TestKit.Base;
using Orleans.Transactions.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class TransactionalStateStorageTestRunnerxUnit<TState> : TransactionalStateStorageTestRunner<TState>
        where TState: class, new()
    {
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
    }
}
