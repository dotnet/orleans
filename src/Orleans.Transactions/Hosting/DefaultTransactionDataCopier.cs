using Orleans.Serialization;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class DefaultTransactionDataCopier<TData> : ITransactionDataCopier<TData>
    {
        private readonly SerializationManager serializationManager;

        public DefaultTransactionDataCopier(SerializationManager serializationManager)
        {
            this.serializationManager = serializationManager;
        }

        public TData DeepCopy(TData original)
        {
            return (TData)this.serializationManager.DeepCopy(original);
        }
    }
}
