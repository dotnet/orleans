
using System;
using System.Collections.Generic;
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
            this.factories = contextAccessor.GrainContext.GetResourceFactoryRegistry<ITransactionManager>();
            this.managers = new Dictionary<string, ITransactionManager>();
        }

        public Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ParticipantId resource) => GetManager(resourceId).Ping(transactionId, timeStamp, resource);

        public Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalResources) => GetManager(resourceId).PrepareAndCommit(transactionId, accessCount, timeStamp, writeResources, totalResources);

        public Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status) => GetManager(resourceId).Prepared(transactionId, timestamp, resource, status);

        private ITransactionManager GetManager(string resourceId)
        {
            if (!this.managers.TryGetValue(resourceId, out ITransactionManager manager))
            {
                this.managers[resourceId] = manager = this.factories[resourceId].Invoke();
            }
            return manager;
        }
    }
}
