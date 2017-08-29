
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    public class TransactionalExtension : ITransactionalExtension
    {
        private readonly Dictionary<string, ITransactionalResource> transactionalResources = new Dictionary<string, ITransactionalResource>();

        public async Task<bool> Prepare(long transactionId, string resourceId, TransactionalResourceVersion? writeVersion, TransactionalResourceVersion? readVersion)
        {
            ITransactionalResource transactionalResource;
            if(transactionalResources.TryGetValue(resourceId, out transactionalResource))
            {
                return await transactionalResource.Prepare(transactionId, writeVersion, readVersion);
            }
            return false;
        }

        public async Task Abort(long transactionId, string resourceId)
        {
            ITransactionalResource transactionalResource;
            if (transactionalResources.TryGetValue(resourceId, out transactionalResource))
            {
                await transactionalResource.Abort(transactionId);
            }
        }

        public async Task Commit(long transactionId, string resourceId)
        {
            ITransactionalResource transactionalResource;
            if (transactionalResources.TryGetValue(resourceId, out transactionalResource))
            {
                await transactionalResource.Commit(transactionId);
            }
        }

        public void Register(string resourceId, ITransactionalResource localTransactionalResource)
        {
            this.transactionalResources.Add(resourceId, localTransactionalResource);
        }
    }
}
