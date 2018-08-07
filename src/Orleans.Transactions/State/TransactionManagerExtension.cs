
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

namespace Orleans.Transactions
{
    public class TransactionManagerExtension : ITransactionManagerExtension
    {
        private readonly ResourceFactoryRegistry<ITransactionManager> factories;
        private readonly Dictionary<string, ITransactionManager> managers;

        public TransactionManagerExtension(IGrainActivationContext context)
        {
            this.factories = context.GetResourceFactoryRegistry<ITransactionManager>();
            this.managers = new Dictionary<string, ITransactionManager>();
        }

        public Task<TransactionalStatus> CommitReadOnly(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
        {
            if (!this.managers.TryGetValue(resourceId, out ITransactionManager manager))
            {
                this.managers[resourceId] = manager = this.factories[resourceId].Invoke();
            }
            return manager.CommitReadOnly(transactionId, accessCount, timeStamp);
        }

        public Task Ping(string resourceId, Guid transactionId, DateTime timeStamp, ParticipantId resource)
        {
            if (!this.managers.TryGetValue(resourceId, out ITransactionManager manager))
            {
                this.managers[resourceId] = manager = this.factories[resourceId].Invoke();
            }
            return manager.Ping(transactionId, timeStamp, resource);
        }

        public Task<TransactionalStatus> PrepareAndCommit(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, List<ParticipantId> writeResources, int totalResources)
        {
            if (!this.managers.TryGetValue(resourceId, out ITransactionManager manager))
            {
                this.managers[resourceId] = manager = this.factories[resourceId].Invoke();
            }
            return manager.PrepareAndCommit(transactionId, accessCount, timeStamp, writeResources, totalResources);
        }

        public Task Prepared(string resourceId, Guid transactionId, DateTime timestamp, ParticipantId resource, TransactionalStatus status)
        {
            if (!this.managers.TryGetValue(resourceId, out ITransactionManager manager))
            {
                this.managers[resourceId] = manager = this.factories[resourceId].Invoke();
            }
            return manager.Prepared(transactionId, timestamp, resource, status);
        }
    }
}
