using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Transactions.Tests
{
    public class TransactionOrchestrationResultGrain : Grain, ITransactionOrchestrationResultGrain
    {
        private readonly TransactionOrchestrationResult orchestrationResult = new TransactionOrchestrationResult();
        private Logger logger;

        public override Task OnActivateAsync()
        {
            this.logger = this.GetLogger(nameof(TransactionOrchestrationResultGrain));
            return Task.CompletedTask;
        }

        public Task RecordPrepare(long transactionId)
        {
            this.logger.Info($"Grain {this.GetPrimaryKey()} prepared transaction {transactionId}.");
            this.orchestrationResult.Prepared.Add(transactionId);
            return Task.CompletedTask;
        }

        public Task RecordAbort(long transactionId)
        {
            this.logger.Info($"Grain {this.GetPrimaryKey()} aborted transaction {transactionId}.");
            this.orchestrationResult.Aborted.Add(transactionId);
            return Task.CompletedTask;
        }

        public Task RecordCommit(long transactionId)
        {
            this.logger.Info($"Grain {this.GetPrimaryKey()} committed transaction {transactionId}.");
            this.orchestrationResult.Committed.Add(transactionId);
            return Task.CompletedTask;
        }

        public Task<TransactionOrchestrationResult> GetResults()
        {
            this.logger.Info($"Reporting transaction orchestration results for Grain {this.GetPrimaryKey()}.");
            return Task.FromResult(this.orchestrationResult);
        }
    }
}
