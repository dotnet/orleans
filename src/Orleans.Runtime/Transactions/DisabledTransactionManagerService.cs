using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions;

namespace Orleans.Transactions
{
    internal class DisabledTransactionManagerService : ITransactionManagerService
    {
        public Task AbortTransaction(long transactionId, OrleansTransactionAbortedException reason)
        {
            throw new OrleansTransactionsDisabledException();
        }

        public Task<CommitTransactionsResponse> CommitTransactions(List<TransactionInfo> transactions, HashSet<long> queries)
        {
            throw new OrleansTransactionsDisabledException();
        }

        public Task<StartTransactionsResponse> StartTransactions(List<TimeSpan> timeouts)
        {
            throw new OrleansTransactionsDisabledException();
        }
    }
}
