using System;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public class TransactionTestRunnerBase
    {
        protected readonly IGrainFactory grainFactory;
        protected readonly ITestOutputHelper output;
        private bool distributedTm;

        protected TransactionTestRunnerBase(IGrainFactory grainFactory, ITestOutputHelper output, bool distributedTm = false)
        {
            this.output = output;
            this.grainFactory = grainFactory;
            this.distributedTm = distributedTm;
        }

        protected ITransactionTestGrain RandomTestGrain(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            return TestGrain(grainStates, Guid.NewGuid());
        }

        protected virtual ITransactionTestGrain TestGrain(TransactionTestConstants.TransactionGrainStates grainStates, Guid id)
        {
            return TestGrain(GetTestGrainClassName(grainStates), id);
        }

        protected ITransactionTestGrain RandomTestGrain(string transactionTestGrainClassNames)
        {
            return TestGrain(transactionTestGrainClassNames, Guid.NewGuid());
        }

        protected virtual ITransactionTestGrain TestGrain(string transactionTestGrainClassName, Guid id)
        {
            return grainFactory.GetGrain<ITransactionTestGrain>(id, transactionTestGrainClassName);
        }

        private string GetTestGrainClassName(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            if(this.distributedTm)
            {
                if (grainStates == TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)
                    return TransactionTestConstants.SingleStateTransactionalGrainDistributedTM;
                throw new SkipException($"{grainStates} not supported when using distributed transaction manager.");
            }

            if (grainStates == TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)
                return TransactionTestConstants.SingleStateTransactionalGrain;
            if (grainStates == TransactionTestConstants.TransactionGrainStates.DoubleStateTransaction)
                return TransactionTestConstants.DoubleStateTransactionalGrain;
            if (grainStates == TransactionTestConstants.TransactionGrainStates.MaxStateTransaction)
                return TransactionTestConstants.MaxStateTransactionalGrain;
            throw new SkipException($"{grainStates} not supported when using distributed transaction manager.");
        }
    }
}
