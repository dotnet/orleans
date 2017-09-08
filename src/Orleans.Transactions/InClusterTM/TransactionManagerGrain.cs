using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions
{
    public interface ITransactionManagerGrain : ITransactionManagerService, IGrainWithIntegerKey
    {
    }

    [Reentrant]
    public class TransactionManagerGrain : Grain, ITransactionManagerGrain
    {
        private readonly ITransactionManager transactionManager;
        private readonly ITransactionManagerService transactionManagerService;

        public TransactionManagerGrain(ITransactionManager transactionManager)
        {
            this.transactionManager = transactionManager;
            this.transactionManagerService = new TransactionManagerService(transactionManager);
        }

        public override async Task OnActivateAsync()
        {
            await transactionManager.StartAsync();
        }

        public override async Task OnDeactivateAsync()
        {
            await transactionManager.StopAsync();
        }

        public Task<StartTransactionsResponse> StartTransactions(List<TimeSpan> timeouts)
        {
            return this.transactionManagerService.StartTransactions(timeouts);
        }

        public Task<CommitTransactionsResponse> CommitTransactions(List<TransactionInfo> transactions, HashSet<long> queries)
        {
            return this.transactionManagerService.CommitTransactions(transactions, queries);
        }

        public Task AbortTransaction(long transactionId, OrleansTransactionAbortedException reason)
        {
            return this.transactionManagerService.AbortTransaction(transactionId, reason);
        }
    }
}
