
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionManagerExtension : ITransactionManagerExtension
    {
        private readonly ResourceFactoryRegistry<ITransactionManager> factories;
        private readonly Dictionary<string, ITransactionManager> managers;

        public TransactionManagerExtension(IGrainContextAccessor contextAccessor)
        {
            this.factories = contextAccessor.GrainContext.GetResourceFactoryRegistry<ITransactionManager>()!;
            this.managers = new Dictionary<string, ITransactionManager>();
        }

        /// <inheritdoc/>
        public Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ParticipantId resource)
            => Ping(resourceId, transactionId, timeStamp, resource, CancellationToken.None);

        /// <inheritdoc/>
        public Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ParticipantId resource, CancellationToken cancellationToken)
        {
            return GetManager(resourceId).Ping(transactionId, timeStamp, resource, cancellationToken);
        }

        /// <inheritdoc/>
        public Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalResources)
            => PrepareAndCommit(resourceId, transactionId, accessCount, timeStamp, writeResources, totalResources, CancellationToken.None);

        /// <inheritdoc/>
        public Task<TransactionalStatus> PrepareAndCommit(
            string resourceId,
            Guid transactionId,
            AccessCounter accessCount,
            DateTime timeStamp,
            List<ParticipantId> writeResources,
            int totalResources,
            CancellationToken cancellationToken)
        {
            return GetManager(resourceId).PrepareAndCommit(transactionId, accessCount, timeStamp, writeResources, totalResources, cancellationToken);
        }

        /// <inheritdoc/>
        public Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status)
            => Prepared(resourceId, transactionId, timestamp, resource, status, CancellationToken.None);

        /// <inheritdoc/>
        public Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status, CancellationToken cancellationToken)
        {
            return GetManager(resourceId).Prepared(transactionId, timestamp, resource, status, cancellationToken);
        }

        private ITransactionManager GetManager(string resourceId)
        {
            if (!this.managers.TryGetValue(resourceId, out ITransactionManager? manager))
            {
                this.managers[resourceId] = manager = this.factories[resourceId].Invoke();
            }
            return manager;
        }
    }
}
