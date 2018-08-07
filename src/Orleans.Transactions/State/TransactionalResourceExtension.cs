
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.State;

namespace Orleans.Transactions
{
    public class TransactionalResourceExtension : ITransactionalResourceExtension
    {
        private readonly ResourceFactoryRegistry<ITransactionalResource> factories;
        private readonly Dictionary<string, ITransactionalResource> resources;

        public TransactionalResourceExtension(IGrainActivationContext context)
        {
            this.factories = context.GetResourceFactoryRegistry<ITransactionalResource>();
            this.resources = new Dictionary<string, ITransactionalResource>();
        }

        public Task Abort(string resourceId, Guid transactionId)
        {
            if(!this.resources.TryGetValue(resourceId, out ITransactionalResource resource))
            {
                this.resources[resourceId] = resource = this.factories[resourceId].Invoke();
            }
            return resource.Abort(transactionId);
        }

        public Task Cancel(string resourceId, Guid transactionId, DateTime timeStamp, TransactionalStatus status)
        {
            if (!this.resources.TryGetValue(resourceId, out ITransactionalResource resource))
            {
                this.resources[resourceId] = resource = this.factories[resourceId].Invoke();
            }
            return resource.Cancel(transactionId, timeStamp, status);
        }

        public Task Confirm(string resourceId, Guid transactionId, DateTime timeStamp)
        {
            if (!this.resources.TryGetValue(resourceId, out ITransactionalResource resource))
            {
                this.resources[resourceId] = resource = this.factories[resourceId].Invoke();
            }
            return resource.Confirm(transactionId, timeStamp);
        }

        public Task Prepare(string resourceId, Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
        {
            if (!this.resources.TryGetValue(resourceId, out ITransactionalResource resource))
            {
                this.resources[resourceId] = resource = this.factories[resourceId].Invoke();
            }
            return resource.Prepare(transactionId, accessCount, timeStamp, transactionManager);
        }
    }
}
