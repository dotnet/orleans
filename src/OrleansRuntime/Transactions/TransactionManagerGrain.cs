using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;

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

        /// <summary>
        /// This method is called at the end of the process of activating a grain.
        /// It is called before any messages have been dispatched to the grain.
        /// For grains with declared persistent state, this method is called after the State property has been populated.
        /// </summary>
        public override async Task OnActivateAsync()
        {
            await transactionManager.StartAsync();
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
