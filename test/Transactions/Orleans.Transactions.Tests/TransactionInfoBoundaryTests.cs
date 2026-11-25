using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Transactions;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public sealed class TransactionInfoBoundaryTests
{
    [Fact]
    public void CopyConstructor_RejectsMissingSource()
    {
        Assert.Throws<ArgumentNullException>("other", () => new TransactionInfo(null!));
    }

    [Fact]
    public void CopyConstructor_PreservesMetadataAndStartsIndependentBranchState()
    {
        var timestamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var original = new TransactionInfo(Guid.NewGuid(), timestamp, timestamp.AddSeconds(1), readOnly: true)
        {
            TryToCommit = false,
            UseExclusiveLock = true,
            Timeout = TimeSpan.FromSeconds(30),
            OriginalException = [1],
            PendingCalls = 2,
        };

        var copy = new TransactionInfo(original);

        Assert.Equal(original.TransactionId, copy.TransactionId);
        Assert.Equal(timestamp, copy.TimeStamp);
        Assert.Equal(timestamp.AddSeconds(1), copy.Priority);
        Assert.True(copy.IsReadOnly);
        Assert.False(copy.TryToCommit);
        Assert.True(copy.UseExclusiveLock);
        Assert.Equal(TimeSpan.FromSeconds(30), copy.Timeout);
        Assert.Equal(0, copy.PendingCalls);
        Assert.Null(copy.OriginalException);
        Assert.NotSame(original.Participants, copy.Participants);
        Assert.Empty(copy.Participants);
    }

    [Fact]
    public void Join_RejectsMissingBranchBeforeChangingReconciliation()
    {
        var transaction = new TransactionInfo();
        var branch = transaction.Fork();
        branch.TimeStamp = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        branch.TryToCommit = false;

        Assert.Throws<ArgumentNullException>("x", () => transaction.Join(null!));

        transaction.Join(branch);
        transaction.ReconcilePending();
        Assert.Equal(0, transaction.PendingCalls);
        Assert.Equal(branch.TimeStamp, transaction.TimeStamp);
        Assert.False(transaction.TryToCommit);
    }

    [Fact]
    public void MustAbort_RequiresSerializerForStoredException()
    {
        byte[] storedException = [1];
        var transaction = new TransactionInfo { OriginalException = storedException };

        Assert.Throws<ArgumentNullException>("serializer", () => transaction.MustAbort(null!));

        Assert.Same(storedException, transaction.OriginalException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MustAbort_WithoutStoredExceptionDoesNotUseSerializer(bool pendingCall)
    {
        var transaction = new TransactionInfo();
        if (pendingCall)
        {
            transaction.Fork();
        }

        var result = transaction.MustAbort(null!);

        if (pendingCall)
        {
            Assert.IsType<OrleansOrphanCallException>(result);
            Assert.Equal(1, transaction.PendingCalls);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Fact]
    public void RecordException_RequiresSerializerBeforeChangingStoredException()
    {
        var transaction = new TransactionInfo();

        Assert.Throws<ArgumentNullException>("sm",
            () => transaction.RecordException(new InvalidOperationException("first failure"), null!));

        Assert.Null(transaction.OriginalException);
    }

    [Fact]
    public void RecordException_PreservesFirstFailureWithoutUsingLaterSerializer()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer<OrleansTransactionAbortedException>>();
        var transaction = new TransactionInfo();
        transaction.RecordException(new InvalidOperationException("first failure"), serializer);
        var storedException = transaction.OriginalException;

        transaction.RecordException(new OperationCanceledException("later failure"), null!);

        Assert.Same(storedException, transaction.OriginalException);
        var aborted = Assert.IsType<OrleansTransactionAbortedException>(transaction.MustAbort(serializer));
        Assert.Equal("first failure", Assert.IsType<InvalidOperationException>(aborted.InnerException).Message);
    }
}
