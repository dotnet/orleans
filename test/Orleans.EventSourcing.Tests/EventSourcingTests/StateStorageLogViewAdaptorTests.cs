using Orleans.EventSourcing;
using Orleans.EventSourcing.Common;
using Orleans.EventSourcing.StateStorage;
using Orleans.Storage;
using Xunit;
using StateStorageAdaptor = Orleans.EventSourcing.StateStorage.LogViewAdaptor<Tester.EventSourcingTests.TestLogView, Tester.EventSourcingTests.TestLogEntry>;
using StateUpdateNotification = Orleans.EventSourcing.StateStorage.LogViewAdaptor<Tester.EventSourcingTests.TestLogView, Tester.EventSourcingTests.TestLogEntry>.UpdateNotificationMessage;

namespace Tester.EventSourcingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
[TestArea("EventSourcing")]
public sealed class StateStorageLogViewAdaptorTests
{
    [Fact]
    public void Merge_WithContiguousSameOriginUnderCap_CombinesInOrderWithoutMutatingInputs()
    {
        var (adaptor, _, _, _) = CreateAdaptor();
        var earlier = Notification(2, "remote", "etag-2", "one", "two");
        var later = Notification(4, "remote", "etag-4", "three", "four");

        var result = Assert.IsType<StateUpdateNotification>(adaptor.MergeForTest(earlier, later));

        Assert.Equal(4, result.Version);
        Assert.Equal("remote", result.Origin);
        Assert.Equal("etag-4", result.ETag);
        Assert.Equal(["one", "two", "three", "four"], result.Updates.Select(entry => entry.Value));
        Assert.NotSame(earlier.Updates, result.Updates);
        Assert.NotSame(later.Updates, result.Updates);
        Assert.Equal(["one", "two"], earlier.Updates.Select(entry => entry.Value));
        Assert.Equal(["three", "four"], later.Updates.Select(entry => entry.Value));
        Assert.Equal((2, "etag-2"), (earlier.Version, earlier.ETag));
        Assert.Equal((4, "etag-4"), (later.Version, later.ETag));
    }

    [Fact]
    public void Merge_AtEntryLimit_Merges199ButFallsBackAt200()
    {
        var (adaptor, _, _, _) = CreateAdaptor();
        var first198 = Enumerable.Range(0, 198).Select(index => $"a-{index}").ToArray();
        var first199 = Enumerable.Range(0, 199).Select(index => $"b-{index}").ToArray();
        var later199 = Notification(199, "remote", "etag-199", "last-199");
        var later200 = Notification(200, "remote", "etag-200", "last-200");

        var merged = Assert.IsType<StateUpdateNotification>(
            adaptor.MergeForTest(Notification(198, "remote", "etag-198", first198), later199));
        var fallback = Assert.IsType<VersionNotificationMessage>(
            adaptor.MergeForTest(Notification(199, "remote", "etag-199", first199), later200));

        Assert.Equal(199, merged.Updates.Count);
        Assert.Equal("a-0", merged.Updates[0].Value);
        Assert.Equal("a-197", merged.Updates[197].Value);
        Assert.Equal("last-199", merged.Updates[198].Value);
        Assert.Equal((199, "etag-199"), (merged.Version, merged.ETag));
        Assert.Equal(200, fallback.Version);
        Assert.Equal(["last-199"], later199.Updates.Select(entry => entry.Value));
        Assert.Equal(["last-200"], later200.Updates.Select(entry => entry.Value));
    }

    [Theory]
    [InlineData("different-origin")]
    [InlineData("gap")]
    [InlineData("overlap")]
    [InlineData("wrong-message")]
    public void Merge_WhenInputsAreIncompatible_ReturnsLaterVersionNotification(string incompatibility)
    {
        var (adaptor, _, _, _) = CreateAdaptor();
        var earlier = Notification(2, "remote", "etag-2", "one", "two");
        var later = incompatibility switch
        {
            "different-origin" => Notification(4, "other", "etag-4", "three", "four"),
            "gap" => Notification(5, "remote", "etag-5", "three", "four"),
            "overlap" => Notification(3, "remote", "etag-3", "three", "four"),
            _ => Notification(4, "remote", "etag-4", "three", "four"),
        };
        INotificationMessage earlierInput = incompatibility == "wrong-message"
            ? new VersionNotificationMessage { Version = earlier.Version }
            : earlier;

        var result = Assert.IsType<VersionNotificationMessage>(adaptor.MergeForTest(earlierInput, later));

        Assert.Equal(later.Version, result.Version);
        Assert.Equal(["one", "two"], earlier.Updates.Select(entry => entry.Value));
        Assert.Equal(["three", "four"], later.Updates.Select(entry => entry.Value));
        Assert.Equal((2, "remote", "etag-2"), (earlier.Version, earlier.Origin, earlier.ETag));
    }

    [Fact]
    public async Task OnNotificationReceived_WithFutureTypedNotification_QueuesUntilPredecessorThenAppliesBothInOrder()
    {
        var (adaptor, host, storage, _) = CreateAdaptor();

        await adaptor.OnProtocolMessageReceived(Notification(2, "remote", "remote-etag-2", "two"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "future state notification to drain");

        Assert.Equal(0, adaptor.ConfirmedVersion);
        Assert.Empty(adaptor.ConfirmedView.Entries);
        Assert.Empty(host.ViewChanges);

        await adaptor.OnProtocolMessageReceived(Notification(1, "remote", "remote-etag-1", "one"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "contiguous state notifications to drain");

        Assert.Equal(2, adaptor.ConfirmedVersion);
        Assert.Equal(["one", "two"], adaptor.ConfirmedView.Entries);
        Assert.Equal([(true, true)], host.ViewChanges);

        Assert.True(await TestPhase.Await(
            adaptor.TryAppend(new TestLogEntry("local")),
            "post-notification state write to commit"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "post-notification state worker to become idle");

        Assert.Equal("remote-etag-2", storage.LastSuppliedETag);
        Assert.Equal(",test-cluster", storage.LastSuppliedWriteVector);
        Assert.Equal(["one", "two", "local"], storage.StateSnapshot.StateAndMetaData.State.Entries);
        Assert.Equal(3, storage.StateSnapshot.StateAndMetaData.GlobalVersion);
    }

    [Fact]
    public async Task OnNotificationReceived_WithStaleTypedNotification_DiscardsWithoutChangingState()
    {
        var (adaptor, host, _, _) = CreateAdaptor();
        await adaptor.OnProtocolMessageReceived(Notification(2, "remote", "etag-2", "one", "two"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "initial state notification to drain");
        var changeCount = host.ViewChanges.Count;

        await adaptor.OnProtocolMessageReceived(Notification(1, "remote", "stale-etag", "stale"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "stale state notification to drain");

        Assert.Equal(2, adaptor.ConfirmedVersion);
        Assert.Equal(["one", "two"], adaptor.ConfirmedView.Entries);
        Assert.Equal(changeCount, host.ViewChanges.Count);
        Assert.Empty(adaptor.UnconfirmedSuffix);
    }

    [Fact]
    public async Task ReadAsync_OnFreshAdaptor_LoadsMaterializedSnapshotAndAllMetadata()
    {
        var (_, _, storage, _) = CreateAdaptor();
        var (writer, _, _, _) = CreateAdaptor(storage);
        Assert.True(await TestPhase.Await(
            writer.TryAppendRange(Entries("one", "two")),
            "materialized state write to commit"));
        await TestPhase.Await(writer.PostOnDeactivate(), "materialized state writer to become idle");
        var persisted = storage.StateSnapshot;

        var (fresh, host, _, _) = CreateAdaptor(storage);
        await Activate(fresh, "fresh state adaptor");

        Assert.Equal(2, fresh.ConfirmedVersion);
        Assert.Equal(["one", "two"], fresh.ConfirmedView.Entries);
        Assert.Equal(",test-cluster", persisted.StateAndMetaData.WriteVector);
        Assert.Equal("etag-1", persisted.ETag);
        Assert.True(persisted.RecordExists);
        Assert.Equal([(true, true)], host.ViewChanges);

        fresh.ConfirmedView.Entries.Add("cache-only");
        Assert.Equal(["one", "two"], storage.StateSnapshot.StateAndMetaData.State.Entries);

        var (metadataReader, _, _, _) = CreateAdaptor(storage);
        await Activate(metadataReader, "state metadata reader");
        Assert.True(await TestPhase.Await(
            metadataReader.TryAppend(new TestLogEntry("three")),
            "state metadata continuation write to commit"));
        await TestPhase.Await(metadataReader.PostOnDeactivate(), "state metadata writer to become idle");
        Assert.Equal("etag-1", storage.LastSuppliedETag);
        Assert.Equal("", storage.LastSuppliedWriteVector);
        Assert.Equal(["one", "two", "three"], storage.StateSnapshot.StateAndMetaData.State.Entries);
        Assert.Equal(3, storage.StateSnapshot.StateAndMetaData.GlobalVersion);
    }

    [Fact]
    public async Task WriteAsync_WhenPersistenceSucceedsThenThrowsAndWriteBitMatches_ClassifiesSuccessWithoutDuplicate()
    {
        var (adaptor, host, storage, _) = CreateAdaptor();
        storage.NextWriteBehavior = StorageWriteBehavior.PersistThenThrow;

        var result = await TestPhase.Await(
            adaptor.TryAppend(new TestLogEntry("once")),
            "ambiguous state write to resolve");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "ambiguous state writer to become idle");

        Assert.True(result);
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal(["once"], storage.StateSnapshot.StateAndMetaData.State.Entries);
        Assert.Equal(["once"], adaptor.ConfirmedView.Entries);
        Assert.Equal(1, adaptor.ConfirmedVersion);
        var issue = Assert.Single(host.ConnectionIssues);
        Assert.IsType<StateStorageAdaptor.UpdateStateStorageFailed>(issue);
        Assert.Equal("persisted write reported failure", ((StateStorageAdaptor.UpdateStateStorageFailed)issue).Exception.Message);
        Assert.Same(issue, Assert.Single(host.ResolvedConnectionIssues));

        Assert.True(await TestPhase.Await(
            adaptor.TryAppend(new TestLogEntry("after-recovery")),
            "state write after ambiguous recovery to commit"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "state writer after ambiguous recovery to become idle");
        Assert.Equal(2, storage.WriteCount);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal("etag-1", storage.LastSuppliedETag);
        Assert.Equal(
            ["once", "after-recovery"],
            storage.StateSnapshot.StateAndMetaData.State.Entries);
    }

    [Fact]
    public async Task WriteAsync_WhenETagConflictBelongsToAnotherWriter_RereadsAndRejectsConditionalRange()
    {
        var (adaptor, host, storage, _) = CreateAdaptor();
        storage.ConflictNextStateWrite(["remote"], 1, ",remote", "remote-etag");

        var result = await TestPhase.Await(
            adaptor.TryAppendRange(Entries("conditional-one", "conditional-two")),
            "conflicting conditional state range to resolve");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "conflicting state writer to become idle");

        Assert.False(result);
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal(1, adaptor.ConfirmedVersion);
        Assert.Equal(["remote"], adaptor.ConfirmedView.Entries);
        Assert.Equal(["remote"], adaptor.TentativeView.Entries);
        Assert.Empty(adaptor.UnconfirmedSuffix);
        Assert.Equal(["remote"], storage.StateSnapshot.StateAndMetaData.State.Entries);
        Assert.IsType<StateStorageAdaptor.UpdateStateStorageFailed>(Assert.Single(host.ConnectionIssues));
        Assert.Single(host.ResolvedConnectionIssues);
    }

    [Fact]
    public async Task ClearLogAsync_WithPendingEntries_ResetsOnceAndFreshAdaptorReadsVersionZero()
    {
        var (adaptor, host, storage, _) = CreateAdaptor();
        var blockedWrite = storage.BlockNextWrite();
        adaptor.Submit(new TestLogEntry("pending-at-clear"));
        await TestPhase.Await(blockedWrite.Started, "state write before clear to start");
        var changesBeforeClear = host.ViewChanges.Count;

        var clear = adaptor.ClearLogAsync(CancellationToken.None);
        Assert.False(clear.IsCompleted);
        blockedWrite.Complete(true);
        await TestPhase.Await(clear, "state clear to complete");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "state clear worker to become idle");

        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(1, storage.ClearCount);
        Assert.Equal(0, adaptor.ConfirmedVersion);
        Assert.Empty(adaptor.ConfirmedView.Entries);
        Assert.Empty(adaptor.TentativeView.Entries);
        Assert.Empty(adaptor.UnconfirmedSuffix);
        Assert.Equal(
            [(false, true), (false, true), (true, true)],
            host.ViewChanges.Skip(changesBeforeClear));
        Assert.Empty(storage.StateSnapshot.StateAndMetaData.State.Entries);
        Assert.Equal(0, storage.StateSnapshot.StateAndMetaData.GlobalVersion);

        var (fresh, _, _, _) = CreateAdaptor(storage);
        await Activate(fresh, "fresh state adaptor after clear");
        Assert.Equal(0, fresh.ConfirmedVersion);
        Assert.Empty(fresh.ConfirmedView.Entries);

        Assert.True(await TestPhase.Await(
            fresh.TryAppend(new TestLogEntry("after-clear")),
            "post-clear state write to commit"));
        await TestPhase.Await(fresh.PostOnDeactivate(), "post-clear state writer to become idle");
        Assert.Equal(["after-clear"], storage.StateSnapshot.StateAndMetaData.State.Entries);
        Assert.Equal(1, fresh.ConfirmedVersion);
    }

    private static async Task Activate(StateStorageAdaptor adaptor, string phase)
    {
        await adaptor.PreOnActivate();
        await adaptor.PostOnActivate();
        await TestPhase.Await(adaptor.PostOnDeactivate(), $"{phase} activation read to finish");
    }

    private static (TestStateStorageAdaptor Adaptor, RecordingLogViewAdaptorHost Host, DeterministicEventSourcingStorage Storage, RecordingProtocolServices Services)
        CreateAdaptor(DeterministicEventSourcingStorage? storage = null)
    {
        var host = new RecordingLogViewAdaptorHost();
        var services = new RecordingProtocolServices();
        storage ??= new DeterministicEventSourcingStorage();
        return (new TestStateStorageAdaptor(host, new TestLogView(), storage, services), host, storage, services);
    }

    private static StateUpdateNotification Notification(
        int version,
        string origin,
        string etag,
        params string[] values) =>
        new()
        {
            Version = version,
            Origin = origin,
            ETag = etag,
            Updates = Entries(values).ToList(),
        };

    private static TestLogEntry[] Entries(params string[] values) =>
        values.Select(value => new TestLogEntry(value)).ToArray();
}

internal sealed class TestStateStorageAdaptor(
    RecordingLogViewAdaptorHost host,
    TestLogView initialState,
    IGrainStorage storage,
    RecordingProtocolServices services)
    : StateStorageAdaptor(host, initialState, storage, "test-state", services)
{
    public INotificationMessage MergeForTest(INotificationMessage earlier, INotificationMessage later) =>
        Merge(earlier, later);
}
