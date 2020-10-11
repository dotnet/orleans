using System;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    internal class DisabledTransactionAgent : ITransactionAgent
    {
        public Task Abort(ITransactionInfo transactionInfo)
        {
            throw new OrleansTransactionsDisabledException();
        }

        public Task<(TransactionalStatus Status, Exception exception)> Resolve(ITransactionInfo transactionInfo)
        {
            throw new OrleansTransactionsDisabledException();
        }

        public Task<ITransactionInfo> StartTransaction(bool readOnly, TimeSpan timeout)
        {
            throw new OrleansStartTransactionFailedException(new OrleansTransactionsDisabledException());
        }
    }
}
