using Orleans.EventSourcing;
using Orleans.EventSourcing.Common;
using Orleans.EventSourcing.LogStorage;
using Orleans.EventSourcing.StateStorage;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;
using LogStorageAdaptor = Orleans.EventSourcing.LogStorage.LogViewAdaptor<Tester.EventSourcingTests.TestLogView, Tester.EventSourcingTests.TestLogEntry>;
using LogUpdateNotification = Orleans.EventSourcing.LogStorage.LogViewAdaptor<Tester.EventSourcingTests.TestLogView, Tester.EventSourcingTests.TestLogEntry>.UpdateNotificationMessage;

namespace Tester.EventSourcingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
[TestArea("EventSourcing")]
public sealed class LogStorageLogViewAdaptorTests
{
    [Fact]
    public void Merge_WithContiguousSameOriginUnderCap_CombinesInOrderWithoutMutatingInputs()
    {
        var (adaptor, _, _, _) = CreateAdaptor();
        var earlier = Notification(2, "remote", "etag-2", "one", "two");
        var later = Notification(4, "remote", "etag-4", "three", "four");

        var result = Assert.IsType<LogUpdateNotification>(adaptor.MergeForTest(earlier, later));

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

        var merged = Assert.IsType<LogUpdateNotification>(
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
        await TestPhase.Await(adaptor.PostOnDeactivate(), "future log notification to drain");

        Assert.Equal(0, adaptor.ConfirmedVersion);
        Assert.Empty(adaptor.ConfirmedView.Entries);
        Assert.Empty(host.ViewChanges);

        await adaptor.OnProtocolMessageReceived(Notification(1, "remote", "remote-etag-1", "one"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "contiguous log notifications to drain");

        Assert.Equal(2, adaptor.ConfirmedVersion);
        Assert.Equal(["one", "two"], adaptor.ConfirmedView.Entries);
        Assert.Equal([(true, true)], host.ViewChanges);

        Assert.True(await TestPhase.Await(
            adaptor.TryAppend(new TestLogEntry("local")),
            "post-notification log write to commit"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "post-notification log worker to become idle");

        Assert.Equal("remote-etag-2", storage.LastSuppliedETag);
        Assert.Equal(",test-cluster", storage.LastSuppliedWriteVector);
        Assert.Equal(["one", "two", "local"], storage.LogSnapshot.StateAndMetaData.Log.Select(entry => entry.Value));
        Assert.Equal(3, storage.LogSnapshot.StateAndMetaData.GlobalVersion);
    }

    [Fact]
    public async Task OnNotificationReceived_WithStaleTypedNotification_DiscardsWithoutChangingState()
    {
        var (adaptor, host, _, _) = CreateAdaptor();
        await adaptor.OnProtocolMessageReceived(Notification(2, "remote", "etag-2", "one", "two"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "initial log notification to drain");
        var changeCount = host.ViewChanges.Count;

        await adaptor.OnProtocolMessageReceived(Notification(1, "remote", "stale-etag", "stale"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "stale log notification to drain");

        Assert.Equal(2, adaptor.ConfirmedVersion);
        Assert.Equal(["one", "two"], adaptor.ConfirmedView.Entries);
        Assert.Equal(changeCount, host.ViewChanges.Count);
        Assert.Empty(adaptor.UnconfirmedSuffix);
    }

    [Fact]
    public async Task ReadAsync_OnFreshAdaptor_ReplaysFullPersistedLogAndReturnsDeepCopiedSegment()
    {
        var (_, _, storage, _) = CreateAdaptor();
        var (writer, _, _, _) = CreateAdaptor(storage);
        Assert.True(await TestPhase.Await(
            writer.TryAppendRange(Entries("zero", "one", "two", "three")),
            "persisted log write to commit"));
        await TestPhase.Await(writer.PostOnDeactivate(), "persisted log writer to become idle");

        var (fresh, host, _, _) = CreateAdaptor(storage);
        await Activate(fresh, "fresh log adaptor");
        var segment = await fresh.RetrieveLogSegment(1, 3);

        Assert.Equal(4, fresh.ConfirmedVersion);
        Assert.Equal(["zero", "one", "two", "three"], fresh.ConfirmedView.Entries);
        Assert.Equal(["one", "two"], segment.Select(entry => entry.Value));
        Assert.Equal([(true, true)], host.ViewChanges);
        var mutableSegment = Assert.IsType<List<TestLogEntry>>(segment);
        mutableSegment.Clear();
        Assert.Equal(["zero", "one", "two", "three"], fresh.ConfirmedView.Entries);
        Assert.Equal(
            ["zero", "one", "two", "three"],
            storage.LogSnapshot.StateAndMetaData.Log.Select(entry => entry.Value));
        Assert.Equal(
            ["one", "two"],
            (await fresh.RetrieveLogSegment(1, 3)).Select(entry => entry.Value));
    }

    [Fact]
    public async Task WriteAsync_WhenPersistenceSucceedsThenThrowsAndWriteBitMatches_ClassifiesSuccessWithoutDuplicate()
    {
        var (adaptor, host, storage, _) = CreateAdaptor();
        storage.NextWriteBehavior = StorageWriteBehavior.PersistThenThrow;

        var result = await TestPhase.Await(
            adaptor.TryAppend(new TestLogEntry("once")),
            "ambiguous log write to resolve");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "ambiguous log writer to become idle");

        Assert.True(result);
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal(["once"], storage.LogSnapshot.StateAndMetaData.Log.Select(entry => entry.Value));
        Assert.Equal(["once"], adaptor.ConfirmedView.Entries);
        Assert.Equal(1, adaptor.ConfirmedVersion);
        var issue = Assert.Single(host.ConnectionIssues);
        Assert.IsType<LogStorageAdaptor.UpdateLogStorageFailed>(issue);
        Assert.Equal("persisted write reported failure", ((LogStorageAdaptor.UpdateLogStorageFailed)issue).Exception.Message);
        Assert.Same(issue, Assert.Single(host.ResolvedConnectionIssues));

        Assert.True(await TestPhase.Await(
            adaptor.TryAppend(new TestLogEntry("after-recovery")),
            "log write after ambiguous recovery to commit"));
        await TestPhase.Await(adaptor.PostOnDeactivate(), "log writer after ambiguous recovery to become idle");
        Assert.Equal(2, storage.WriteCount);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal("etag-1", storage.LastSuppliedETag);
        Assert.Equal(
            ["once", "after-recovery"],
            storage.LogSnapshot.StateAndMetaData.Log.Select(entry => entry.Value));
    }

    [Fact]
    public async Task WriteAsync_WhenETagConflictBelongsToAnotherWriter_RereadsAndRejectsConditionalRange()
    {
        var (adaptor, host, storage, _) = CreateAdaptor();
        storage.ConflictNextLogWrite(["remote"], ",remote", "remote-etag");

        var result = await TestPhase.Await(
            adaptor.TryAppendRange(Entries("conditional-one", "conditional-two")),
            "conflicting conditional log range to resolve");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "conflicting log writer to become idle");

        Assert.False(result);
        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(1, storage.ReadCount);
        Assert.Equal(1, adaptor.ConfirmedVersion);
        Assert.Equal(["remote"], adaptor.ConfirmedView.Entries);
        Assert.Equal(["remote"], adaptor.TentativeView.Entries);
        Assert.Empty(adaptor.UnconfirmedSuffix);
        Assert.Equal(["remote"], storage.LogSnapshot.StateAndMetaData.Log.Select(entry => entry.Value));
        Assert.IsType<LogStorageAdaptor.UpdateLogStorageFailed>(Assert.Single(host.ConnectionIssues));
        Assert.Single(host.ResolvedConnectionIssues);
    }

    [Fact]
    public async Task ClearLogAsync_WithPendingEntries_ResetsOnceAndFreshAdaptorReadsVersionZero()
    {
        var (adaptor, host, storage, _) = CreateAdaptor();
        var blockedWrite = storage.BlockNextWrite();
        adaptor.Submit(new TestLogEntry("pending-at-clear"));
        await TestPhase.Await(blockedWrite.Started, "log write before clear to start");
        var changesBeforeClear = host.ViewChanges.Count;

        var clear = adaptor.ClearLogAsync(CancellationToken.None);
        Assert.False(clear.IsCompleted);
        blockedWrite.Complete(true);
        await TestPhase.Await(clear, "log clear to complete");
        await TestPhase.Await(adaptor.PostOnDeactivate(), "log clear worker to become idle");

        Assert.Equal(1, storage.WriteCount);
        Assert.Equal(1, storage.ClearCount);
        Assert.Equal(0, adaptor.ConfirmedVersion);
        Assert.Empty(adaptor.ConfirmedView.Entries);
        Assert.Empty(adaptor.TentativeView.Entries);
        Assert.Empty(adaptor.UnconfirmedSuffix);
        Assert.Equal(
            [(false, true), (false, true), (true, true)],
            host.ViewChanges.Skip(changesBeforeClear));
        Assert.Empty(storage.LogSnapshot.StateAndMetaData.Log);

        var (fresh, _, _, _) = CreateAdaptor(storage);
        await Activate(fresh, "fresh log adaptor after clear");
        Assert.Equal(0, fresh.ConfirmedVersion);
        Assert.Empty(fresh.ConfirmedView.Entries);

        Assert.True(await TestPhase.Await(
            fresh.TryAppend(new TestLogEntry("after-clear")),
            "post-clear log write to commit"));
        await TestPhase.Await(fresh.PostOnDeactivate(), "post-clear log writer to become idle");
        Assert.Equal(["after-clear"], storage.LogSnapshot.StateAndMetaData.Log.Select(entry => entry.Value));
        Assert.Equal(1, fresh.ConfirmedVersion);
    }

    private static async Task Activate(LogStorageAdaptor adaptor, string phase)
    {
        await adaptor.PreOnActivate();
        await adaptor.PostOnActivate();
        await TestPhase.Await(adaptor.PostOnDeactivate(), $"{phase} activation read to finish");
    }

    private static (TestLogStorageAdaptor Adaptor, RecordingLogViewAdaptorHost Host, DeterministicEventSourcingStorage Storage, RecordingProtocolServices Services)
        CreateAdaptor(DeterministicEventSourcingStorage? storage = null)
    {
        var host = new RecordingLogViewAdaptorHost();
        var services = new RecordingProtocolServices();
        storage ??= new DeterministicEventSourcingStorage();
        return (new TestLogStorageAdaptor(host, new TestLogView(), storage, services), host, storage, services);
    }

    private static LogUpdateNotification Notification(
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

internal sealed class TestLogStorageAdaptor(
    RecordingLogViewAdaptorHost host,
    TestLogView initialState,
    IGrainStorage storage,
    RecordingProtocolServices services)
    : LogStorageAdaptor(host, initialState, storage, "test-log", services)
{
    public INotificationMessage MergeForTest(INotificationMessage earlier, INotificationMessage later) =>
        Merge(earlier, later);
}

internal enum StorageWriteBehavior
{
    Success,
    PersistThenThrow,
    Conflict,
}

internal sealed class DeterministicEventSourcingStorage : IGrainStorage
{
    private LogStateWithMetaDataAndETag<TestLogEntry> _log = new();
    private GrainStateWithMetaDataAndETag<TestLogView> _state = new(new TestLogView());
    private LogStateWithMetaDataAndETag<TestLogEntry>? _conflictingLog;
    private GrainStateWithMetaDataAndETag<TestLogView>? _conflictingState;
    private ControlledOperation<bool>? _blockedWrite;
    private int _etag;

    public StorageWriteBehavior NextWriteBehavior { get; set; }

    public int ReadCount { get; private set; }

    public int WriteCount { get; private set; }

    public int ClearCount { get; private set; }

    public string? LastSuppliedETag { get; private set; }

    public string? LastSuppliedWriteVector { get; private set; }

    public LogStateWithMetaDataAndETag<TestLogEntry> LogSnapshot => Clone(_log);

    public GrainStateWithMetaDataAndETag<TestLogView> StateSnapshot => Clone(_state);

    public ControlledOperation<bool> BlockNextWrite()
    {
        _blockedWrite = new ControlledOperation<bool>();
        return _blockedWrite;
    }

    public void ConflictNextLogWrite(IEnumerable<string> values, string writeVector, string etag)
    {
        _conflictingLog = new LogStateWithMetaDataAndETag<TestLogEntry>
        {
            ETag = etag,
            RecordExists = true,
            StateAndMetaData = new LogStateWithMetaData<TestLogEntry>
            {
                Log = values.Select(value => new TestLogEntry(value)).ToList(),
                WriteVector = writeVector,
            },
        };
        NextWriteBehavior = StorageWriteBehavior.Conflict;
    }

    public void ConflictNextStateWrite(
        IEnumerable<string> values,
        int version,
        string writeVector,
        string etag)
    {
        _conflictingState = new GrainStateWithMetaDataAndETag<TestLogView>
        {
            ETag = etag,
            RecordExists = true,
            StateAndMetaData = new GrainStateWithMetaData<TestLogView>
            {
                State = new TestLogView(values),
                GlobalVersion = version,
                WriteVector = writeVector,
            },
        };
        NextWriteBehavior = StorageWriteBehavior.Conflict;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        ReadCount++;
        switch ((object)grainState)
        {
            case LogStateWithMetaDataAndETag<TestLogEntry> log:
                Copy(_log, log);
                break;
            case GrainStateWithMetaDataAndETag<TestLogView> state:
                Copy(_state, state);
                break;
            default:
                throw new InvalidOperationException($"Unsupported state type {typeof(T)}.");
        }

        await Task.CompletedTask;
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        WriteCount++;
        var blockedWrite = _blockedWrite;
        _blockedWrite = null;

        switch ((object)grainState)
        {
            case LogStateWithMetaDataAndETag<TestLogEntry> log:
                LastSuppliedETag = log.ETag;
                LastSuppliedWriteVector = log.StateAndMetaData.WriteVector;
                if (blockedWrite is not null)
                {
                    blockedWrite.SignalStarted();
                    await blockedWrite.Completion;
                }

                if (NextWriteBehavior == StorageWriteBehavior.Conflict)
                {
                    _log = Clone(_conflictingLog ?? throw new InvalidOperationException("No conflicting log was configured."));
                    NextWriteBehavior = StorageWriteBehavior.Success;
                    throw new InvalidOperationException("log etag conflict");
                }

                var logEtag = $"etag-{++_etag}";
                if (NextWriteBehavior == StorageWriteBehavior.PersistThenThrow)
                {
                    _log = Clone(log);
                    _log.ETag = logEtag;
                    _log.RecordExists = true;
                }
                else
                {
                    log.ETag = logEtag;
                    log.RecordExists = true;
                    _log = Clone(log);
                }
                break;
            case GrainStateWithMetaDataAndETag<TestLogView> state:
                LastSuppliedETag = state.ETag;
                LastSuppliedWriteVector = state.StateAndMetaData.WriteVector;
                if (blockedWrite is not null)
                {
                    blockedWrite.SignalStarted();
                    await blockedWrite.Completion;
                }

                if (NextWriteBehavior == StorageWriteBehavior.Conflict)
                {
                    _state = Clone(_conflictingState ?? throw new InvalidOperationException("No conflicting state was configured."));
                    NextWriteBehavior = StorageWriteBehavior.Success;
                    throw new InvalidOperationException("state etag conflict");
                }

                var stateEtag = $"etag-{++_etag}";
                if (NextWriteBehavior == StorageWriteBehavior.PersistThenThrow)
                {
                    _state = Clone(state);
                    _state.ETag = stateEtag;
                    _state.RecordExists = true;
                }
                else
                {
                    state.ETag = stateEtag;
                    state.RecordExists = true;
                    _state = Clone(state);
                }
                break;
            default:
                throw new InvalidOperationException($"Unsupported state type {typeof(T)}.");
        }

        if (NextWriteBehavior == StorageWriteBehavior.PersistThenThrow)
        {
            NextWriteBehavior = StorageWriteBehavior.Success;
            throw new InvalidOperationException("persisted write reported failure");
        }
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        ClearCount++;
        switch ((object)grainState)
        {
            case LogStateWithMetaDataAndETag<TestLogEntry>:
                _log = new LogStateWithMetaDataAndETag<TestLogEntry>();
                break;
            case GrainStateWithMetaDataAndETag<TestLogView>:
                _state = new GrainStateWithMetaDataAndETag<TestLogView>(new TestLogView());
                break;
            default:
                throw new InvalidOperationException($"Unsupported state type {typeof(T)}.");
        }

        return Task.CompletedTask;
    }

    private static LogStateWithMetaDataAndETag<TestLogEntry> Clone(
        LogStateWithMetaDataAndETag<TestLogEntry> source) =>
        new()
        {
            ETag = source.ETag,
            RecordExists = source.RecordExists,
            StateAndMetaData = new LogStateWithMetaData<TestLogEntry>
            {
                Log = source.StateAndMetaData.Log.Select(entry => new TestLogEntry(entry.Value)).ToList(),
                WriteVector = source.StateAndMetaData.WriteVector,
            },
        };

    private static GrainStateWithMetaDataAndETag<TestLogView> Clone(
        GrainStateWithMetaDataAndETag<TestLogView> source) =>
        new()
        {
            ETag = source.ETag,
            RecordExists = source.RecordExists,
            StateAndMetaData = new GrainStateWithMetaData<TestLogView>
            {
                State = source.StateAndMetaData.State.Copy(),
                GlobalVersion = source.StateAndMetaData.GlobalVersion,
                WriteVector = source.StateAndMetaData.WriteVector,
            },
        };

    private static void Copy(
        LogStateWithMetaDataAndETag<TestLogEntry> source,
        LogStateWithMetaDataAndETag<TestLogEntry> destination)
    {
        var copy = Clone(source);
        destination.StateAndMetaData = copy.StateAndMetaData;
        destination.ETag = copy.ETag;
        destination.RecordExists = copy.RecordExists;
    }

    private static void Copy(
        GrainStateWithMetaDataAndETag<TestLogView> source,
        GrainStateWithMetaDataAndETag<TestLogView> destination)
    {
        var copy = Clone(source);
        destination.StateAndMetaData = copy.StateAndMetaData;
        destination.ETag = copy.ETag;
        destination.RecordExists = copy.RecordExists;
    }
}
