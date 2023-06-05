
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
            factories = contextAccessor.GrainContext.GetResourceFactoryRegistry<ITransactionManager>();
            managers = new Dictionary<string, ITransactionManager>();
        }

        public Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ParticipantId resource) => GetManager(resourceId).Ping(transactionId, timeStamp, resource);

        public Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalResources) => GetManager(resourceId).PrepareAndCommit(transactionId, accessCount, timeStamp, writeResources, totalResources);

        public Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status) => GetManager(resourceId).Prepared(transactionId, timestamp, resource, status);

        private ITransactionManager GetManager(string resourceId)
        {
            if (!managers.TryGetValue(resourceId, out var manager))
            {
                managers[resourceId] = manager = factories[resourceId].Invoke();
            }
            return manager;
        }
    }
}
