using System;
using System.Threading.Tasks;
using Orleans.Transaction.Testkit;
using Orleans.Transactions.Abstractions;
using Xunit;

namespace Orleans.Transactions.Testkit.Xunit
{
    public abstract class TransactionalStateStorageTestRunnerXunit<TState> : TransactionalStateStorageTestRunner<TState>
        where TState: class, new()
    {
        public TransactionalStateStorageTestRunnerXunit(Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory,
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
