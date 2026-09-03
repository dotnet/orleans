using Orleans.EventSourcing;
using Orleans.EventSourcing.Common;
using Xunit;

namespace Tester.EventSourcingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
[TestArea("EventSourcing")]
public sealed class PrimaryBasedLogViewAdaptorTests
{
    [Fact]
    public async Task TryAppendRange_WithNonEmptyRange_AppendsAtomicallyInOrderAndCompletesTrue()
    {
        var (adaptor, host, _) = CreateAdaptor();
        var write = adaptor.QueueWrite();

        var append = adaptor.TryAppendRange(Entries("one", "two", "three"));
        await TestPhase.Await(write.Started, "non-empty conditional range write to start");

        Assert.False(append.IsCompleted);
        Assert.Equal(["one", "two", "three"], adaptor.TentativeView.Entries);
        Assert.Equal(["one", "two", "three"], adaptor.UnconfirmedSuffix.Select(entry => entry.Value));

        write.Complete(3);
        Assert.True(await TestPhase.Await(append, "non-empty conditional range to commit"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "non-empty conditional range worker to become idle");

        Assert.Equal(3, adaptor.ConfirmedVersion);
        Assert.Equal(["one", "two", "three"], adaptor.ConfirmedView.Entries);
        Assert.Empty(adaptor.UnconfirmedSuffix);
        Assert.Equal(
            [(true, false), (true, false), (true, false), (false, true), (false, true)],
            host.ViewChanges);
        Assert.Equal(["write:start", "write:end"], adaptor.OperationLog);
    }

    [Fact]
    public async Task TryAppendRange_WithEmptyRange_CompletesTrueWithoutSchedulingStorage()
    {
        var (adaptor, host, _) = CreateAdaptor();

        var append = adaptor.TryAppendRange([]);

        Assert.True(append.IsCompletedSuccessfully);
        Assert.True(await append);
        Assert.Equal(0, adaptor.WriteCount);
        Assert.Empty(adaptor.UnconfirmedSuffix);
        Assert.Empty(adaptor.ConfirmedView.Entries);
        Assert.Empty(adaptor.TentativeView.Entries);
        Assert.Empty(host.ViewChanges);
        Assert.Empty(adaptor.OperationLog);
    }

    [Fact]
    public async Task TryAppendRange_WhenActivationReadAdvancesVersion_CompletesFalseAndRemovesWholeRange()
    {
        var (adaptor, host, _) = CreateAdaptor();
        var read = adaptor.QueueRead(new TestLogView(["remote-one", "remote-two"]), 2);
        await adaptor.PreOnActivate();
        await adaptor.PostOnActivate();
        await TestPhase.Await(read.Started, "activation read to start");

        var append = adaptor.TryAppendRange(Entries("conditional-one", "conditional-two"));
        Assert.False(append.IsCompleted);
        Assert.Equal(
            ["conditional-one", "conditional-two"],
            adaptor.UnconfirmedSuffix.Select(entry => entry.Value));

        read.Complete(new PrimaryReadResult(new TestLogView(["remote-one", "remote-two"]), 2));
        Assert.False(await TestPhase.Await(append, "stale conditional range to be rejected"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "conflict worker to become idle");

        Assert.Equal(2, adaptor.ConfirmedVersion);
        Assert.Equal(["remote-one", "remote-two"], adaptor.ConfirmedView.Entries);
        Assert.Empty(adaptor.UnconfirmedSuffix);
        Assert.Equal(["remote-one", "remote-two"], adaptor.TentativeView.Entries);
        Assert.Equal(0, adaptor.WriteCount);
        Assert.Equal(
            [(true, false), (true, false), (true, true), (true, false)],
            host.ViewChanges);
    }

    [Fact]
    public async Task TryAppendRange_WithSingleUseEnumerable_EnumeratesOnceAndPreservesOrder()
    {
        var (adaptor, _, _) = CreateAdaptor();
        var entries = new SingleUseEnumerable<TestLogEntry>(Entries("first", "second", "third"));
        var write = adaptor.QueueWrite();

        var append = adaptor.TryAppendRange(entries);
        await TestPhase.Await(write.Started, "single-use range write to start");
        write.Complete(3);

        Assert.True(await TestPhase.Await(append, "single-use range to commit"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "single-use range worker to become idle");
        Assert.Equal(1, entries.EnumerationCount);
        Assert.Equal(["first", "second", "third"], adaptor.ConfirmedView.Entries);
        Assert.Equal(3, adaptor.ConfirmedVersion);
    }

    [Fact]
    public async Task OnNotificationReceived_WithVersionNotification_RefreshesToAdvertisedVersion()
    {
        var (adaptor, host, _) = CreateAdaptor();
        var read = adaptor.QueueRead(new TestLogView(["remote"]), 4);

        var response = await adaptor.OnProtocolMessageReceived(new VersionNotificationMessage { Version = 4 });
        await TestPhase.Await(read.Started, "version-notification refresh read to start");
        Assert.Null(response);
        Assert.Equal(0, adaptor.ConfirmedVersion);

        read.Complete(new PrimaryReadResult(new TestLogView(["remote"]), 4));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "version-notification refresh to finish");

        Assert.Equal(4, adaptor.ConfirmedVersion);
        Assert.Equal(["remote"], adaptor.ConfirmedView.Entries);
        Assert.Equal([("VersionNotificationMessage", 4)], adaptor.NotificationTrace);
        Assert.Equal([(true, true)], host.ViewChanges);
        Assert.Equal(["read:start", "read:end"], adaptor.OperationLog);
    }

    [Fact]
    public async Task OnNotificationReceived_WithNestedBatch_ProcessesChildrenInOrder()
    {
        var (adaptor, _, _) = CreateAdaptor();
        var read = adaptor.QueueRead(new TestLogView(["latest"]), 5);
        var notification = new BatchedNotificationMessage
        {
            Notifications =
            [
                new VersionNotificationMessage { Version = 2 },
                new BatchedNotificationMessage
                {
                    Notifications =
                    [
                        new VersionNotificationMessage { Version = 3 },
                        new VersionNotificationMessage { Version = 5 },
                    ],
                },
            ],
        };

        await adaptor.OnProtocolMessageReceived(notification);
        await TestPhase.Await(read.Started, "nested-batch refresh read to start");
        read.Complete(new PrimaryReadResult(new TestLogView(["latest"]), 5));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "nested-batch refresh to finish");

        Assert.Equal(
            [
                ("BatchedNotificationMessage", 5),
                ("VersionNotificationMessage", 2),
                ("BatchedNotificationMessage", 5),
                ("VersionNotificationMessage", 3),
                ("VersionNotificationMessage", 5),
            ],
            adaptor.NotificationTrace);
        Assert.Equal(1, adaptor.ReadCount);
        Assert.Equal(5, adaptor.ConfirmedVersion);
        Assert.Equal(["latest"], adaptor.ConfirmedView.Entries);
    }

    [Fact]
    public async Task OnNotificationReceived_WithUnknownMessage_ThrowsProtocolTransportException()
    {
        var (adaptor, host, _) = CreateAdaptor();

        var exception = await Assert.ThrowsAsync<ProtocolTransportException>(
            () => adaptor.OnProtocolMessageReceived(new UnknownNotificationMessage(7)));

        Assert.Equal(
            $"message type {typeof(UnknownNotificationMessage).FullName} not handled by OnNotificationReceived",
            exception.Message);
        Assert.Equal(0, adaptor.ConfirmedVersion);
        Assert.Empty(adaptor.ConfirmedView.Entries);
        Assert.Empty(host.ViewChanges);
        Assert.Empty(adaptor.OperationLog);
    }

    [Fact]
    public async Task OnNotificationReceived_DuringBlockedWrite_RunsInSubsequentWorkerCycle()
    {
        var (adaptor, _, _) = CreateAdaptor();
        var write = adaptor.QueueWrite();
        var read = adaptor.QueueRead(new TestLogView(["local", "remote-two", "remote-three", "remote-four"]), 4);

        var append = adaptor.TryAppend(new TestLogEntry("local"));
        await TestPhase.Await(write.Started, "write to block before notification");
        await adaptor.OnProtocolMessageReceived(new VersionNotificationMessage { Version = 4 });

        Assert.False(read.Started.IsCompleted);
        Assert.Equal(1, adaptor.MaximumConcurrentPrimaryOperations);

        write.Complete(1);
        Assert.True(await TestPhase.Await(append, "blocked write to commit"));
        await TestPhase.Await(read.Started, "notification refresh to start after write");
        read.Complete(new PrimaryReadResult(
            new TestLogView(["local", "remote-two", "remote-three", "remote-four"]),
            4));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "notification-after-write worker to become idle");

        Assert.Equal(
            ["write:start", "write:end", "read:start", "read:end"],
            adaptor.OperationLog);
        Assert.Equal(1, adaptor.MaximumConcurrentPrimaryOperations);
        Assert.Equal(4, adaptor.ConfirmedVersion);
        Assert.Equal(["local", "remote-two", "remote-three", "remote-four"], adaptor.ConfirmedView.Entries);
    }

    [Fact]
    public async Task Synchronize_DuringBlockedWrite_SerializesWriteThenRead()
    {
        var (adaptor, _, _) = CreateAdaptor();
        var write = adaptor.QueueWrite();
        var read = adaptor.QueueRead(new TestLogView(["local", "remote"]), 2);

        var append = adaptor.TryAppend(new TestLogEntry("local"));
        await TestPhase.Await(write.Started, "write to block before synchronize");
        var synchronize = adaptor.Synchronize();

        Assert.False(synchronize.IsCompleted);
        Assert.False(read.Started.IsCompleted);
        write.Complete(1);
        Assert.True(await TestPhase.Await(append, "write before synchronize to commit"));
        await TestPhase.Await(read.Started, "synchronize refresh read to start");
        Assert.False(synchronize.IsCompleted);

        read.Complete(new PrimaryReadResult(new TestLogView(["local", "remote"]), 2));
        await TestPhase.Await(synchronize, "synchronize refresh to complete");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "synchronize worker to become idle");

        Assert.Equal(
            ["write:start", "write:end", "read:start", "read:end"],
            adaptor.OperationLog);
        Assert.Equal(1, adaptor.MaximumConcurrentPrimaryOperations);
        Assert.Equal(2, adaptor.ConfirmedVersion);
        Assert.Equal(["local", "remote"], adaptor.ConfirmedView.Entries);
    }

    [Fact]
    public async Task Work_WhenViewChangedCallbackThrows_ReportsCaughtUserCodeException()
    {
        var (adaptor, host, services) = CreateAdaptor();
        var write = adaptor.QueueWrite();
        host.ThrowOnNextConfirmedChange();

        var append = adaptor.TryAppend(new TestLogEntry("committed"));
        await TestPhase.Await(write.Started, "write before callback failure");
        write.Complete(1);

        Assert.True(await TestPhase.Await(append, "write despite callback failure"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "callback-failure worker to become idle");
        var reported = Assert.Single(services.UserCodeExceptions);
        Assert.Equal("OnViewChanged", reported.Callback);
        Assert.Equal("NotifyViewChanges", reported.Where);
        Assert.Equal("view-change callback failed", reported.Exception.Message);
        Assert.Empty(services.ProtocolErrors);
        Assert.Equal(1, adaptor.ConfirmedVersion);
        Assert.Equal(["committed"], adaptor.ConfirmedView.Entries);
    }

    [Fact]
    public async Task ClearLogAsync_ConcurrentCallers_CoalesceAndResetOnlyAfterRelease()
    {
        var (adaptor, host, _) = CreateAdaptor();
        await Commit(adaptor, "confirmed");
        var changesBeforeClear = host.ViewChanges.Count;
        var clearOperation = adaptor.QueueClear();

        var first = adaptor.ClearLogAsync(CancellationToken.None);
        await TestPhase.Await(clearOperation.Started, "coalesced clear to start");
        adaptor.Submit(new TestLogEntry("tentative"));
        var second = adaptor.ClearLogAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        Assert.Equal(["confirmed"], adaptor.ConfirmedView.Entries);
        Assert.Equal(["confirmed", "tentative"], adaptor.TentativeView.Entries);
        Assert.Equal(1, adaptor.ClearCount);

        clearOperation.Complete(true);
        await TestPhase.Await(Task.WhenAll(first, second), "coalesced clear callers to complete");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "coalesced clear worker to become idle");

        Assert.Equal(1, adaptor.ClearCount);
        Assert.Equal(0, adaptor.ConfirmedVersion);
        Assert.Empty(adaptor.ConfirmedView.Entries);
        Assert.Empty(adaptor.TentativeView.Entries);
        Assert.Empty(adaptor.UnconfirmedSuffix);
        Assert.Equal([(true, false), (true, true)], host.ViewChanges.Skip(changesBeforeClear));
        Assert.Equal(["clear:start", "clear:end"], adaptor.OperationLog.TakeLast(2));
    }

    [Fact]
    public async Task ClearLogAsync_WhenStorageClearFails_PropagatesAndPreservesConfirmedAndTentativeState()
    {
        var (adaptor, host, _) = CreateAdaptor();
        await Commit(adaptor, "confirmed");
        var changesBeforeClear = host.ViewChanges.Count;
        var clearOperation = adaptor.QueueClear();
        var pendingWrite = adaptor.QueueWrite();
        var failure = new InvalidOperationException("clear storage failed");

        var clear = adaptor.ClearLogAsync(CancellationToken.None);
        await TestPhase.Await(clearOperation.Started, "failing clear to start");
        adaptor.Submit(new TestLogEntry("tentative"));
        clearOperation.Fail(failure);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestPhase.Await(clear, "failing clear to propagate"));
        await TestPhase.Await(pendingWrite.Started, "pending write after failed clear to start");

        Assert.Same(failure, actual);
        Assert.Equal(1, adaptor.ConfirmedVersion);
        Assert.Equal(["confirmed"], adaptor.ConfirmedView.Entries);
        Assert.Equal(["confirmed", "tentative"], adaptor.TentativeView.Entries);
        Assert.Equal(["tentative"], adaptor.UnconfirmedSuffix.Select(entry => entry.Value));
        Assert.Equal([(true, false)], host.ViewChanges.Skip(changesBeforeClear));
        Assert.Equal(
            ["write:start", "write:end", "clear:start", "clear:end", "write:start"],
            adaptor.OperationLog);

        pendingWrite.Complete(1);
        await TestPhase.Await(adaptor.PostOnDeactivate(), "failed-clear cleanup write to finish");
    }

    private static async Task Commit(TestPrimaryBasedLogViewAdaptor adaptor, string value)
    {
        var write = adaptor.QueueWrite();
        var append = adaptor.TryAppend(new TestLogEntry(value));
        await TestPhase.Await(write.Started, $"setup write '{value}' to start");
        write.Complete(1);
        Assert.True(await TestPhase.Await(append, $"setup write '{value}' to commit"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), $"setup write '{value}' worker to become idle");
    }

    private static (TestPrimaryBasedLogViewAdaptor Adaptor, RecordingLogViewAdaptorHost Host, RecordingProtocolServices Services)
        CreateAdaptor()
    {
        var host = new RecordingLogViewAdaptorHost();
        var services = new RecordingProtocolServices();
        return (new TestPrimaryBasedLogViewAdaptor(host, new TestLogView(), services), host, services);
    }

    private static TestLogEntry[] Entries(params string[] values) =>
        values.Select(value => new TestLogEntry(value)).ToArray();

    private sealed record UnknownNotificationMessage(int Version) : INotificationMessage;
}
