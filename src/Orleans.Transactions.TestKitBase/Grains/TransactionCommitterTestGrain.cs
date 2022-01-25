using System.Threading;
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

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainId().ToString());
            return base.OnActivateAsync(cancellationToken);
        }

        public Task Commit(ITransactionCommitOperation<IRemoteCommitService> operation)
        {
            return this.committer.OnCommit(operation);
        }
    }
}
