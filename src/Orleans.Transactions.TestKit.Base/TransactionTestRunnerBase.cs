using System;

namespace Orleans.Transactions.TestKit
{
    public class TransactionTestRunnerBase
    {
        protected readonly IGrainFactory grainFactory;
        protected readonly Action<string> testOutput;

        protected TransactionTestRunnerBase(IGrainFactory grainFactory, Action<string> testOutput)
        {
            this.grainFactory = grainFactory;
            this.testOutput = testOutput;
        }

        protected ITransactionTestGrain RandomTestGrain(string transactionTestGrainClassNames) => RandomTestGrain<ITransactionTestGrain>(transactionTestGrainClassNames);

        protected TGrainInterface RandomTestGrain<TGrainInterface>(string transactionTestGrainClassNames)
            where TGrainInterface : IGrainWithGuidKey => TestGrain<TGrainInterface>(transactionTestGrainClassNames, Guid.NewGuid());

        protected virtual ITransactionTestGrain TestGrain(string transactionTestGrainClassName, Guid id) => TestGrain<ITransactionTestGrain>(transactionTestGrainClassName, id);

        protected virtual TGrainInterface TestGrain<TGrainInterface>(string transactionTestGrainClassName, Guid id)
            where TGrainInterface : IGrainWithGuidKey => grainFactory.GetGrain<TGrainInterface>(id, $"{typeof(TGrainInterface).Namespace}.{transactionTestGrainClassName}");
    }
}
