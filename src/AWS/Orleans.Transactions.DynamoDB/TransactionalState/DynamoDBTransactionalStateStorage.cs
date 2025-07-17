using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.DynamoDB.TransactionalState;

public class DynamoDBTransactionalStateStorage<TState> : ITransactionalStateStorage<TState> where TState : class, new()
{
    public Task<TransactionalStorageLoadResponse<TState>> Load() => throw new System.NotImplementedException();

    public Task<string> Store(string expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TState>> statesToPrepare, long? commitUpTo,
        long? abortAfter) =>
        throw new System.NotImplementedException();
}
