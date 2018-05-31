using System;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public class TransactionTestRunnerBase
    {
        protected readonly IGrainFactory grainFactory;
        protected readonly ITestOutputHelper output;

        protected TransactionTestRunnerBase(IGrainFactory grainFactory, ITestOutputHelper output)
        {
            this.output = output;
            this.grainFactory = grainFactory;
        }

        protected ITransactionTestGrain RandomTestGrain(string transactionTestGrainClassNames)
        {
            return RandomTestGrain<ITransactionTestGrain>(transactionTestGrainClassNames);
        }

        protected TGrainInterface RandomTestGrain<TGrainInterface>(string transactionTestGrainClassNames)
            where TGrainInterface : IGrainWithGuidKey
        {
            return TestGrain<TGrainInterface>(transactionTestGrainClassNames, Guid.NewGuid());
        }

        protected virtual ITransactionTestGrain TestGrain(string transactionTestGrainClassName, Guid id)
        {
            return TestGrain<ITransactionTestGrain>(transactionTestGrainClassName, id);
        }

        protected virtual TGrainInterface TestGrain<TGrainInterface>(string transactionTestGrainClassName, Guid id)
            where TGrainInterface : IGrainWithGuidKey
        {
            return grainFactory.GetGrain<TGrainInterface>(id, $"{typeof(TGrainInterface).Namespace}.{transactionTestGrainClassName}");
        }
    }
}
