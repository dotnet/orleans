
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionalResourceExtension : ITransactionalResourceExtension
    {
        private readonly ResourceFactoryRegistry<ITransactionalResource> factories;
        private readonly Dictionary<string, ITransactionalResource> resources;

        public TransactionalResourceExtension(IGrainContextAccessor contextAccessor)
        {
            this.factories = contextAccessor.GrainContext.GetResourceFactoryRegistry<ITransactionalResource>();
            this.resources = new Dictionary<string, ITransactionalResource>();
        }

        public Task<TransactionalStatus> CommitReadOnly(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp) => GetResource(resourceId).CommitReadOnly(transactionId, accessCount, timeStamp);

        public Task Abort(string resourceId, Guid transactionId) => GetResource(resourceId).Abort(transactionId);

        public Task Cancel(string resourceId, Guid transactionId, DateTime timeStamp, TransactionalStatus status) => GetResource(resourceId).Cancel(transactionId, timeStamp, status);

        public Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp) => GetResource(resourceId).Confirm(transactionId, timeStamp);

        public Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager) => GetResource(resourceId).Prepare(transactionId, accessCount, timeStamp, transactionManager);

        private ITransactionalResource GetResource(string resourceId)
        {
            if (!this.resources.TryGetValue(resourceId, out ITransactionalResource resource))
            {
                this.resources[resourceId] = resource = this.factories[resourceId].Invoke();
            }
            return resource;
        }
    }
}
