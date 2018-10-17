using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Orleans.Runtime;
using Orleans.Transactions;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.Testkit;

namespace Orleans.Transaction.Testkit
{
    public abstract class TransactionalStateStorageTestRunner<TState> : TransactionTestRunnerBase
        where TState : class, new()
    {
        protected Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory;
        protected Func<TState> stateFactory;
        protected TransactionalStateStorageTestRunner(Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory, Func<TState> stateFactory, IGrainFactory grainFactory)
            :base(grainFactory)
        {
            this.stateStorageFactory = stateStorageFactory;
            this.stateFactory = stateFactory;
        }

        public virtual async Task FirstTime_Load_ShouldReturnEmptyLoadResponse()
        {
            var stateStorage = await this.stateStorageFactory();
            var response = await stateStorage.Load();

            //Assertion
            response.Should().NotBeNull();
            response.ETag.Should().BeNull();
            response.CommittedSequenceId.Should().Be(0);
            response.PendingStates.Should().BeEmpty();
        }

    }
}
