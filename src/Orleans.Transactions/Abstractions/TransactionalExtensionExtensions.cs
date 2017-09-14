using System;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Transactions.Abstractions
{
    public static class TransactionalExtensionExtensions
    {
        public static ITransactionalResource AsTransactionalResource(this ITransactionalExtension transactionalExtension, string resourceId)
        {
            return new TransactionalResourceExtensionWrapper(transactionalExtension, resourceId);
        }

        [Serializable]
        [Immutable]
        internal sealed class TransactionalResourceExtensionWrapper : ITransactionalResource
        {
            private readonly ITransactionalExtension extension;
            private readonly string resourceId;

            public TransactionalResourceExtensionWrapper(ITransactionalExtension transactionalExtension, string resourceId)
            {
                this.extension = transactionalExtension;
                this.resourceId = resourceId;
            }

            public Task<bool> Prepare(long transactionId, TransactionalResourceVersion? writeVersion, TransactionalResourceVersion? readVersion)
            {
                return this.extension.Prepare(transactionId, resourceId, writeVersion, readVersion);
            }

            public Task Abort(long transactionId)
            {
                return this.extension.Abort(transactionId, resourceId);
            }

            public Task Commit(long transactionId)
            {
                return this.extension.Commit(transactionId, resourceId);
            }

            public bool Equals(ITransactionalResource other)
            {
                return Equals((object)other);
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != this.GetType()) return false;
                return Equals((TransactionalResourceExtensionWrapper)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((extension?.GetHashCode() ?? 0) * 397) ^ (resourceId?.GetHashCode() ?? 0);
                }
            }

            private bool Equals(TransactionalResourceExtensionWrapper other)
            {
                return Equals(extension, other.extension) && string.Equals(resourceId, other.resourceId);
            }
        }
    }
}
