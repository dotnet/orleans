using System;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    internal class DisabledTransactionAgent : ITransactionAgent
    {
        public Task Abort(TransactionInfo transactionInfo)
        {
            throw new OrleansTransactionsDisabledException();
        }

        public Task<(TransactionalStatus Status, Exception exception)> Resolve(TransactionInfo transactionInfo)
        {
            throw new OrleansTransactionsDisabledException();
        }

        public Task<TransactionInfo> StartTransaction(bool readOnly, TimeSpan timeout)
        {
            throw new OrleansStartTransactionFailedException(new OrleansTransactionsDisabledException());
        }
    }
}
