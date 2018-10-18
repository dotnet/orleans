using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit.Base
{
    public abstract class TransactionalStateStorageTestRunner<TState> : TransactionTestRunnerBase
        where TState : class, new()
    {
        protected Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory;
        protected Func<TState> stateFactory;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="stateStorageFactory">factory to create ITransactionalStateStorage, the test runner are assuming the state 
        /// in storage is empty when ITransactionalStateStorage was created </param>
        /// <param name="stateFactory">factory to create TState for test</param>
        /// <param name="grainFactory">grain Factory needed for test runner</param>
        /// <param name="testOutput">test output to helpful messages</param>
        protected TransactionalStateStorageTestRunner(Func<Task<ITransactionalStateStorage<TState>>> stateStorageFactory, Func<TState> stateFactory, 
            IGrainFactory grainFactory, Action<string> testOutput)
            :base(grainFactory, testOutput)
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
