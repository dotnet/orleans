using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public class TransactionManagerService : ITransactionManagerService
    {
        private readonly ITransactionManager tm;

        public TransactionManagerService(ITransactionManager tm)
        {
            this.tm = tm;
        }

        public Task<StartTransactionsResponse> StartTransactions(List<TimeSpan> timeouts)
        {
            var result = new StartTransactionsResponse { TransactionId = new List<long>() };

            foreach (var timeout in timeouts)
            {
                result.TransactionId.Add(tm.StartTransaction(timeout));
            }

            result.ReadOnlyTransactionId = tm.GetReadOnlyTransactionId();
            result.AbortLowerBound = result.ReadOnlyTransactionId;

            return Task.FromResult(result);
        }

        public Task<CommitTransactionsResponse> CommitTransactions(List<TransactionInfo> transactions, HashSet<long> queries)
        {
            List<Task> tasks = new List<Task>();

            var result = new CommitTransactionsResponse { CommitResult = new Dictionary<long, CommitResult>() };

            foreach (var ti in transactions)
            {
                try
                {
                    tm.CommitTransaction(ti);
                }
                catch (OrleansTransactionAbortedException e)
                {
                    var cr = new CommitResult()
                    {
                        Success = false,
                        AbortingException = e,
                    };
                    result.CommitResult[ti.TransactionId] = cr;
                }
            }

            foreach (var q in queries)
            {
                OrleansTransactionAbortedException abortingException;
                var status = tm.GetTransactionStatus(q, out abortingException);
                if (status == TransactionStatus.InProgress)
                {
                    continue;
                }

                var cr = new CommitResult();
                if (status == TransactionStatus.Aborted)
                {
                    cr.Success = false;
                    cr.AbortingException = abortingException;
                }
                else if (status == TransactionStatus.Committed)
                {
                    cr.Success = true;
                }
                else if (status == TransactionStatus.Unknown)
                {
                    // Note that the way we communicate an unknown transaction is a false Success
                    // and a null aborting exception.
                    // TODO: make this more explicit?
                    cr.Success = false;
                }

                result.CommitResult[q] = cr;
            }

            result.ReadOnlyTransactionId = tm.GetReadOnlyTransactionId();
            result.AbortLowerBound = result.ReadOnlyTransactionId;

            return Task.FromResult(result);
        }

        public Task AbortTransaction(long transactionId, OrleansTransactionAbortedException reason)
        {
            tm.AbortTransaction(transactionId, reason);
            return Task.CompletedTask;
        }
    }
}
