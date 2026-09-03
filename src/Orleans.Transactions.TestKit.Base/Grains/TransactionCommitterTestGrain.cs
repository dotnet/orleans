using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Enlists remote commit operations in transactions.
    /// </summary>
    public class TransactionCommitterTestGrain : Grain, ITransactionCommitterTestGrain
    {
        /// <summary>
        /// The transaction committer used to enlist remote operations.
        /// </summary>
        protected ITransactionCommitter<IRemoteCommitService> committer;
        private readonly ILoggerFactory loggerFactory;

        /// <summary>
        /// The logger for this grain activation.
        /// </summary>
        protected ILogger logger = null!;

        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionCommitterTestGrain"/> class.
        /// </summary>
        /// <param name="committer">The transaction committer.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public TransactionCommitterTestGrain(
            [TransactionCommitter(TransactionTestConstants.RemoteCommitService, TransactionTestConstants.TransactionStore)] ITransactionCommitter<IRemoteCommitService> committer,
            ILoggerFactory loggerFactory)
        {
            this.committer = committer;
            this.loggerFactory = loggerFactory;
        }

        /// <inheritdoc/>
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger = this.loggerFactory.CreateLogger(this.GetGrainId().ToString());
            return base.OnActivateAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public Task Commit(ITransactionCommitOperation<IRemoteCommitService> operation)
        {
            return this.committer.OnCommit(operation);
        }
    }
}
