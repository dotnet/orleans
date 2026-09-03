using System;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Provides grain reference creation and output services for transaction test runners.
    /// </summary>
    public class TransactionTestRunnerBase
    {
        /// <summary>
        /// The grain factory used to create references for test operations.
        /// </summary>
        protected readonly IGrainFactory grainFactory;

        /// <summary>
        /// The callback used to write test diagnostics.
        /// </summary>
        protected readonly Action<string> testOutput;

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionTestRunnerBase"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to create test grain references.</param>
        /// <param name="testOutput">The callback used to write test diagnostics.</param>
        protected TransactionTestRunnerBase(IGrainFactory grainFactory, Action<string> testOutput)
        {
            this.grainFactory = grainFactory;
            this.testOutput = testOutput;
        }

        /// <summary>
        /// Creates a transaction test grain reference with a new random key.
        /// </summary>
        /// <param name="transactionTestGrainClassNames">The transaction test grain implementation class name.</param>
        /// <returns>A transaction test grain reference.</returns>
        protected ITransactionTestGrain RandomTestGrain(string transactionTestGrainClassNames)
        {
            return RandomTestGrain<ITransactionTestGrain>(transactionTestGrainClassNames);
        }

        /// <summary>
        /// Creates a test grain reference with a new random key.
        /// </summary>
        /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
        /// <param name="transactionTestGrainClassNames">The transaction test grain implementation class name.</param>
        /// <returns>A test grain reference.</returns>
        protected TGrainInterface RandomTestGrain<TGrainInterface>(string transactionTestGrainClassNames)
            where TGrainInterface : IGrainWithGuidKey
        {
            return TestGrain<TGrainInterface>(transactionTestGrainClassNames, Guid.NewGuid());
        }

        /// <summary>
        /// Creates a transaction test grain reference with the specified key.
        /// </summary>
        /// <param name="transactionTestGrainClassName">The transaction test grain implementation class name.</param>
        /// <param name="id">The grain key.</param>
        /// <returns>A transaction test grain reference.</returns>
        protected virtual ITransactionTestGrain TestGrain(string transactionTestGrainClassName, Guid id)
        {
            return TestGrain<ITransactionTestGrain>(transactionTestGrainClassName, id);
        }

        /// <summary>
        /// Creates a test grain reference with the specified key.
        /// </summary>
        /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
        /// <param name="transactionTestGrainClassName">The transaction test grain implementation class name.</param>
        /// <param name="id">The grain key.</param>
        /// <returns>A test grain reference.</returns>
        protected virtual TGrainInterface TestGrain<TGrainInterface>(string transactionTestGrainClassName, Guid id)
            where TGrainInterface : IGrainWithGuidKey
        {
            return grainFactory.GetGrain<TGrainInterface>(id, $"{typeof(TGrainInterface).Namespace}.{transactionTestGrainClassName}");
        }
    }
}
