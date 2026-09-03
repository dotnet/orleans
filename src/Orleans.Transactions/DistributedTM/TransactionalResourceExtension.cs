
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    /// <summary>
    /// Dispatches transaction protocol messages to transactional resources registered on the current grain.
    /// </summary>
    public class TransactionalResourceExtension : ITransactionalResourceExtension
    {
        private readonly ResourceFactoryRegistry<ITransactionalResource> factories;
        private readonly Dictionary<string, ITransactionalResource> resources;

        /// <summary>
        /// Initializes a new extension for the current grain context.
        /// </summary>
        /// <param name="contextAccessor">The accessor for the grain context containing registered transactional resource factories.</param>
        public TransactionalResourceExtension(IGrainContextAccessor contextAccessor)
        {
            this.factories = contextAccessor.GrainContext.GetResourceFactoryRegistry<ITransactionalResource>()!;
            this.resources = new Dictionary<string, ITransactionalResource>();
        }

        /// <inheritdoc/>
        public Task<TransactionalStatus> CommitReadOnly(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
            => CommitReadOnly(resourceId, transactionId, accessCount, timeStamp, CancellationToken.None);

        /// <inheritdoc/>
        public Task<TransactionalStatus> CommitReadOnly(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, CancellationToken cancellationToken)
        {
            return GetResource(resourceId).CommitReadOnly(transactionId, accessCount, timeStamp, cancellationToken);
        }

        /// <inheritdoc/>
        public Task Abort(string resourceId, Guid transactionId)
            => Abort(resourceId, transactionId, CancellationToken.None);

        /// <inheritdoc/>
        public Task Abort(string resourceId, Guid transactionId, CancellationToken cancellationToken)
        {
            return GetResource(resourceId).Abort(transactionId, cancellationToken);
        }

        /// <inheritdoc/>
        public Task Cancel(string resourceId, Guid transactionId, DateTime timeStamp, TransactionalStatus status)
            => Cancel(resourceId, transactionId, timeStamp, status, CancellationToken.None);

        /// <inheritdoc/>
        public Task Cancel(string resourceId, Guid transactionId, DateTime timeStamp, TransactionalStatus status, CancellationToken cancellationToken)
        {
            return GetResource(resourceId).Cancel(transactionId, timeStamp, status, cancellationToken);
        }

        /// <inheritdoc/>
        public Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp)
            => Confirm(resourceId, transactionId, timeStamp, CancellationToken.None);

        /// <inheritdoc/>
        public Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp, CancellationToken cancellationToken)
        {
            return GetResource(resourceId).Confirm(transactionId, timeStamp, cancellationToken);
        }

        /// <inheritdoc/>
        public Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
            => Prepare(resourceId, transactionId, accessCount, timeStamp, transactionManager, CancellationToken.None);

        /// <inheritdoc/>
        public Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager, CancellationToken cancellationToken)
        {
            return GetResource(resourceId).Prepare(transactionId, accessCount, timeStamp, transactionManager, cancellationToken);
        }

        private ITransactionalResource GetResource(string resourceId)
        {
            if (!this.resources.TryGetValue(resourceId, out ITransactionalResource? resource))
            {
                this.resources[resourceId] = resource = this.factories[resourceId].Invoke();
            }
            return resource;
        }
    }
}
