using System;
using System.Threading.Tasks;
using Orleans.Transactions.Testkit.Base;
using Orleans.Transactions.Abstractions;
using Xunit;

namespace Orleans.Transactions.Testkit.xUnit
{
    public abstract class TransactionalStateStorageTestRunnerxUnit<TState> : TransactionalStateStorageTestRunner<TState>
        where TState: class, new()
    {
        public TransactionalStateStorageTestRunnerxUnit(Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory,
            Func<TState> stateFactory, IGrainFactory grainFactory)
            : base(stateStorageFactory, stateFactory, grainFactory)
        {
        }

        [Fact]
        public override Task FirstTime_Load_ShouldReturnEmptyLoadResponse()
        {
            return base.FirstTime_Load_ShouldReturnEmptyLoadResponse();
        }
    }
}
