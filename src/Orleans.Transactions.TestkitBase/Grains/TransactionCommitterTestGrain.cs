using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    public class TransactionCommitterTestGrain : Grain, ITransactionCommitterTestGrain
    {
        protected ITransactionCommitter<IRemoteCommitService> committer;
        private readonly ILoggerFactory loggerFactory;
        protected ILogger logger;

        public TransactionCommitterTestGrain(
            [TransactionCommitter(TransactionTestConstants.RemoteCommitService, TransactionTestConstants.TransactionStore)] ITransactionCommitter<IRemoteCommitService> committer,
            ILoggerFactory loggerFactory)
        {
            this.committer = committer;
            this.loggerFactory = loggerFactory;
        }

        public override Task OnActivateAsync()
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainIdentity().ToString());
            return base.OnActivateAsync();
        }

        public Task Commit(ITransactionCommitOperation<IRemoteCommitService> operation)
        {
            return this.committer.OnCommit(operation);
        }
    }
}
