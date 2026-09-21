#nullable enable

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NSubstitute;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace UnitTests.Dissemination;

public partial class DisseminationProtocolTests
{
    [Fact]
    public async Task MembershipOwnerRechecksCancellationAfterSharedRefreshCompleted()
    {
        var read = new TaskCompletionSource<MembershipTableData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var table = Substitute.For<IMembershipTable>();
        table.ReadAllAsync(Arg.Any<CancellationToken>()).Returns(read.Task);
        using var manager = CreateMembershipReviewManager(CreateSilo(39601), out _, table);
        var refresh = manager.Refresh(cancellationToken: TestContext.Current.CancellationToken);
        var context = new MembershipReviewContinuationContext();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var incoming = CreateMembershipSnapshot(
            2, CreateMembershipEntry(CreateSilo(39602), SiloStatus.Active, DateTime.UnixEpoch));
        Task gossip;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            gossip = ((IMembershipManager)manager).ProcessGossipSnapshot(incoming, cancellation.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.False(gossip.IsCompleted);
        read.SetResult(new MembershipTableData(new TableVersion(1, "1")));
        var continuation = await context.TakeContinuation(TestContext.Current.CancellationToken);
        await refresh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var committed = manager.MembershipTableSnapshot;
        Assert.Equal(new MembershipVersion(1), committed.Version);

        cancellation.Cancel();
        continuation.Callback(continuation.State);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gossip.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Same(committed, manager.MembershipTableSnapshot);
        Assert.DoesNotContain(CreateSilo(39602), manager.MembershipTableSnapshot.Entries.Keys);
    }

    private sealed class MembershipReviewContinuationContext : SynchronizationContext
    {
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _continuations =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();

        public override void Post(SendOrPostCallback callback, object? state) =>
            _continuations.Writer.TryWrite((callback, state));

        public async Task<(SendOrPostCallback Callback, object? State)> TakeContinuation(CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            return await _continuations.Reader.ReadAsync(deadline.Token);
        }
    }
}
