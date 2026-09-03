using Orleans.Transactions.Abstractions;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

public class TransactionCancellationCompatibilityTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT"), TestCategory("Transactions")]
    public async Task TransactionalResourceCancellationOverload_ForwardsToLegacyImplementation()
    {
        ITransactionalResource resource = new LegacyResource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transactionId = Guid.NewGuid();

        await resource.Abort(transactionId, cancellation.Token);

        Assert.Equal(transactionId, ((LegacyResource)resource).AbortedTransaction);
    }

    private sealed class LegacyResource : ITransactionalResource
    {
        public Guid AbortedTransaction { get; private set; }

        public Task<TransactionalStatus> CommitReadOnly(Guid transactionId, AccessCounter accessCount, DateTime timeStamp)
            => Task.FromResult(TransactionalStatus.Ok);

        public Task Prepare(Guid transactionId, AccessCounter accessCount, DateTime timeStamp, ParticipantId transactionManager)
            => Task.CompletedTask;

        public Task Abort(Guid transactionId)
        {
            AbortedTransaction = transactionId;
            return Task.CompletedTask;
        }

        public Task Cancel(Guid transactionId, DateTime timeStamp, TransactionalStatus status) => Task.CompletedTask;
        public Task Confirm(Guid transactionId, DateTime timeStamp) => Task.CompletedTask;
    }
}
