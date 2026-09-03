
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Creates transaction committers for the current grain activation.
    /// </summary>
    public class TransactionCommitterFactory : ITransactionCommitterFactory
    {
        private readonly IGrainContextAccessor contextAccessor;

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionCommitterFactory"/> class.
        /// </summary>
        /// <param name="contextAccessor">The accessor for the current grain activation context.</param>
        public TransactionCommitterFactory(IGrainContextAccessor contextAccessor)
        {
            this.contextAccessor = contextAccessor;
        }

        /// <inheritdoc/>
        public ITransactionCommitter<TService> Create<TService>(ITransactionCommitterConfiguration config) where TService : class
        {
            var currentContext = contextAccessor.GrainContext;
            TransactionCommitter<TService> transactionalState = ActivatorUtilities.CreateInstance<TransactionCommitter<TService>>(currentContext.ActivationServices, config, this.contextAccessor);
            transactionalState.Participate(currentContext.ObservableLifecycle);
            return transactionalState;
        }
    }
}
