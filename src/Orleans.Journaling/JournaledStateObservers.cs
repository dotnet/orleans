using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.Journaling;

/// <summary>
/// Delivers staged changes and commit watermarks from one journaled state manager to its watchers.
/// </summary>
/// <remarks>
/// <para>
/// A manager creates at most one hub, when its state is first watched. Watchers are keyed by state machine
/// instance, so state which nobody watches publishes nothing. Each change is tagged with the version of the
/// journal write which will capture it. Each acknowledged write publishes one watermark which commits every
/// change tagged with an equal or lower version.
/// </para>
/// <para>
/// The hub and its watchers are confined to the scheduler which created the hub: the owning activation's scheduler,
/// where grain code, the manager's work loop, and watch enumeration take turns. Their state therefore uses plain fields.
/// <see cref="StateWatcher.Signal"/> is the only member which other threads call, from cancellation callbacks.
/// </para>
/// </remarks>
internal sealed class JournaledStateObservers
{
    private readonly Dictionary<IStateMachine, StateTopic> _topics = new(ReferenceEqualityComparer.Instance);
    private readonly TaskScheduler _scheduler = TaskScheduler.Current;
    private long _committedVersion;
    private Exception? _failure;
    private bool _isCompleted;

    public JournaledStateObservers(JournaledStateManager manager, long committedVersion)
    {
        Manager = manager;
        _committedVersion = committedVersion;
    }

    public JournaledStateManager Manager { get; }

    [Conditional("DEBUG")]
    public void AssertConfined() =>
        Debug.Assert(TaskScheduler.Current == _scheduler, "Observation state is confined to the scheduler which created it.");

    /// <summary>
    /// Gets the watchers of <paramref name="state"/>, if it is watched.
    /// </summary>
    public bool TryGetTopic(IStateMachine state, [NotNullWhen(true)] out StateTopic? topic)
    {
        AssertConfined();
        return _topics.TryGetValue(state, out topic);
    }

    public void Add(StateWatcher watcher)
    {
        AssertConfined();
        if (_isCompleted)
        {
            watcher.Attach(_committedVersion);
            watcher.Complete(_failure);
            return;
        }

        if (!_topics.TryGetValue(watcher.State, out var topic))
        {
            topic = new StateTopic(this);
            _topics.Add(watcher.State, topic);
        }

        topic.Add(watcher);
        watcher.Attach(_committedVersion);
    }

    public void Remove(StateWatcher watcher)
    {
        AssertConfined();
        if (_topics.TryGetValue(watcher.State, out var topic) && topic.Remove(watcher) && topic.IsEmpty)
        {
            _topics.Remove(watcher.State);
        }
    }

    public void OnWriteCaptured(long version)
    {
        AssertConfined();
        foreach (var topic in _topics.Values)
        {
            topic.OnWriteCaptured(version);
        }
    }

    public void OnWriteCommitted(long version)
    {
        AssertConfined();
        Debug.Assert(version > _committedVersion);
        _committedVersion = version;
        foreach (var topic in _topics.Values)
        {
            topic.OnWatermark(version);
        }
    }

    public void Complete(Exception? failure)
    {
        AssertConfined();
        if (_isCompleted)
        {
            return;
        }

        _isCompleted = true;
        _failure = failure;
        foreach (var topic in _topics.Values)
        {
            topic.Complete(failure);
        }
    }
}

/// <summary>
/// The watchers of one state machine instance.
/// </summary>
internal sealed class StateTopic(JournaledStateObservers hub)
{
    private readonly List<StateWatcher> _watchers = [];

    public bool IsEmpty => _watchers.Count == 0;

    /// <summary>
    /// Delivers a change which the next journal write captures.
    /// </summary>
    public void PublishStaged<TChange>(TChange change) => Publish(change, hub.Manager.StagingWriteVersion);

    /// <summary>
    /// Delivers a change which reflects durable state, such as recovered or deleted state.
    /// </summary>
    /// <remarks>
    /// Durable changes are published while no captured write awaits acknowledgement, so they are committed.
    /// </remarks>
    public void PublishDurable<TChange>(TChange change)
    {
        Debug.Assert(hub.Manager.CapturedWriteVersion == hub.Manager.CommittedWriteVersion, "Durable changes are published while no write is in flight.");
        Publish(change, hub.Manager.CapturedWriteVersion);
    }

    private void Publish<TChange>(TChange change, long version)
    {
        hub.AssertConfined();
        foreach (var watcher in _watchers)
        {
            ((IChangeSink<TChange>)watcher).OnChange(change, version);
        }
    }

    /// <summary>
    /// Wakes every watcher which reads the state directly.
    /// </summary>
    public void SignalAll()
    {
        hub.AssertConfined();
        foreach (var watcher in _watchers)
        {
            watcher.Signal();
        }
    }

    /// <summary>
    /// Wakes the watchers which read the state directly and whose mode includes <paramref name="visibility"/>.
    /// </summary>
    public void Signal(DurableWatchMode visibility)
    {
        Debug.Assert(visibility is not DurableWatchMode.Committed, "Committed changes become visible through watermarks.");
        hub.AssertConfined();
        foreach (var watcher in _watchers)
        {
            if ((watcher.Mode & visibility) != 0)
            {
                watcher.Signal();
            }
        }
    }

    internal void Add(StateWatcher watcher) => _watchers.Add(watcher);

    internal bool Remove(StateWatcher watcher) => _watchers.Remove(watcher);

    internal void OnWriteCaptured(long version)
    {
        foreach (var watcher in _watchers)
        {
            watcher.OnWriteCaptured(version);
        }
    }

    internal void OnWatermark(long version)
    {
        foreach (var watcher in _watchers)
        {
            watcher.OnWatermark(version);
        }
    }

    internal void Complete(Exception? failure)
    {
        foreach (var watcher in _watchers)
        {
            watcher.Complete(failure);
        }
    }
}

/// <summary>
/// Receives changes published to a <see cref="StateTopic"/>.
/// </summary>
internal interface IChangeSink<TChange>
{
    void OnChange(TChange change, long version);
}

/// <summary>
/// A watchable collection which creates snapshots of its complete current contents.
/// </summary>
internal interface IResyncSource<TChange>
{
    /// <summary>
    /// Gets a value indicating whether the collection has journal entries which no captured write contains.
    /// </summary>
    bool HasStagedEntries { get; }

    TChange CreateResync();
}

/// <summary>
/// One reader of a watched state machine.
/// </summary>
/// <remarks>Members other than <see cref="Signal"/> are confined to the hub's scheduler.</remarks>
internal abstract class StateWatcher(JournaledStateObservers hub, IStateMachine state, DurableWatchMode mode)
{
    private readonly SingleWaiterAutoResetEvent _signal = new() { RunContinuationsAsynchronously = true };
    private Exception? _failure;
    private bool _isCompleted;

    public JournaledStateObservers Hub { get; } = hub;

    public IStateMachine State { get; } = state;

    public DurableWatchMode Mode { get; } = mode;

    /// <summary>
    /// Gets a value indicating whether the watcher delivers staged changes and watermarks.
    /// </summary>
    public bool IncludesStaged => (Mode & DurableWatchMode.Staged) != 0;

    protected long CommittedVersion { get; set; }

    public void Signal() => _signal.Signal();

    public ValueTask WaitAsync() => _signal.WaitAsync();

    /// <summary>
    /// Returns <see langword="true"/> when the hub has completed; throws when it completed with a failure.
    /// </summary>
    public bool CheckCompleted()
    {
        Hub.AssertConfined();
        if (!_isCompleted)
        {
            return false;
        }

        if (_failure is { } failure)
        {
            throw new InvalidOperationException(
                "The journaled state manager stopped after a failed operation. A new activation recovers the durable state.",
                failure);
        }

        return true;
    }

    internal void Attach(long committedVersion)
    {
        CommittedVersion = committedVersion;
        OnAttached();
    }

    /// <summary>
    /// Called when the manager captures the write with <paramref name="version"/>, before its storage operation begins.
    /// </summary>
    internal virtual void OnWriteCaptured(long version)
    {
    }

    internal virtual void OnWatermark(long version)
    {
        CommittedVersion = version;
        Signal();
    }

    internal void Complete(Exception? failure)
    {
        _isCompleted = true;
        _failure = failure;
        Signal();
    }

    protected virtual void OnAttached()
    {
    }
}

/// <summary>
/// A watcher which delivers events of type <typeparamref name="TEvent"/>.
/// </summary>
internal abstract class StateWatcher<TEvent>(JournaledStateObservers hub, IStateMachine state, DurableWatchMode mode)
    : StateWatcher(hub, state, mode)
{
    /// <summary>
    /// Reads the next deliverable event.
    /// </summary>
    public abstract bool TryRead(out TEvent value);
}

/// <summary>
/// Buffers a bounded number of collection changes and resynchronizes the reader after an overflow.
/// </summary>
/// <remarks>
/// An overflow discards the buffered changes and folds them, and every later change, into one pending resync. The resync's
/// snapshot is built once: when the write which captures the folded changes is captured, or when a reader with
/// <see cref="DurableWatchMode.Staged"/> reads it, whichever comes first.
/// </remarks>
internal sealed class ChangeWatcher<TChange> : StateWatcher<TChange>, IChangeSink<TChange>
    where TChange : struct, IDurableChange<TChange>
{
    private readonly IResyncSource<TChange> _source;
    private readonly Queue<(TChange Change, long Version)> _buffer = new();
    private readonly int _capacity;
    private TChange _initialChange;
    private long _initialVersion;

    // The version of the pending resync which replaces every discarded change. Zero when none.
    // Every change made while a resync is pending has the same version, since a capture materializes the resync.
    private long _resyncVersion;

    // The lowest and highest versions delivered as staged and not yet confirmed by a watermark. Zero when none.
    private long _unconfirmedVersion;
    private long _lastStagedVersion;

    public ChangeWatcher(
        JournaledStateObservers hub,
        IStateMachine state,
        IResyncSource<TChange> source,
        DurableWatchMode mode,
        int capacity,
        TChange initialChange,
        long initialVersion)
        : base(hub, state, mode)
    {
        Debug.Assert(capacity > 0);
        _source = source;
        _capacity = capacity;
        _initialChange = initialChange;
        _initialVersion = initialVersion;
    }

    protected override void OnAttached()
    {
        OnChange(_initialChange, _initialVersion);
        _initialChange = default;
    }

    public void OnChange(TChange change, long version)
    {
        if (version <= CommittedVersion)
        {
            // Durable resyncs replace every buffered change, and the staged changes already delivered which they discard.
            _buffer.Clear();
            _resyncVersion = 0;
            _unconfirmedVersion = 0;
            _lastStagedVersion = 0;
            _buffer.Enqueue((change, version));
            Signal();
            return;
        }

        if (_resyncVersion != 0)
        {
            Debug.Assert(_resyncVersion == version);
        }
        else if (_buffer.Count >= _capacity)
        {
            _buffer.Clear();
            _resyncVersion = version;
        }
        else
        {
            _buffer.Enqueue((change, version));
        }

        if (IncludesStaged)
        {
            Signal();
        }
    }

    internal override void OnWriteCaptured(long version)
    {
        if (_resyncVersion != 0)
        {
            Debug.Assert(_resyncVersion == version);
            MaterializeResync();
        }
    }

    internal override void OnWatermark(long version)
    {
        CommittedVersion = version;
        if ((_buffer.TryPeek(out var head) && head.Version <= version) || (_unconfirmedVersion != 0 && _unconfirmedVersion <= version))
        {
            Signal();
        }
    }

    public override bool TryRead(out TChange change)
    {
        Hub.AssertConfined();
        if (_resyncVersion != 0 && IncludesStaged)
        {
            MaterializeResync();
        }

        // An eligible watermark precedes further changes, so a producer which keeps staging cannot defer it indefinitely.
        if (_unconfirmedVersion != 0 && _unconfirmedVersion <= CommittedVersion)
        {
            change = TChange.CreateWatermark(CommittedVersion);
            _unconfirmedVersion = _lastStagedVersion > CommittedVersion ? CommittedVersion + 1 : 0;
            return true;
        }

        if (_buffer.TryPeek(out var head))
        {
            if (head.Version <= CommittedVersion)
            {
                _buffer.Dequeue();
                change = head.Change.WithStatus(DurableChangeStatus.Committed, head.Version);
                return true;
            }

            if (IncludesStaged)
            {
                _buffer.Dequeue();
                if (_unconfirmedVersion == 0)
                {
                    _unconfirmedVersion = head.Version;
                }

                _lastStagedVersion = head.Version;
                change = head.Change.WithStatus(DurableChangeStatus.Staged, head.Version);
                return true;
            }
        }

        change = default;
        return false;
    }

    // The snapshot contains every change made so far, all of which carry the resync's version.
    private void MaterializeResync()
    {
        Debug.Assert(_buffer.Count == 0);
        _buffer.Enqueue((_source.CreateResync(), _resyncVersion));
        _resyncVersion = 0;
    }
}
/// <summary>
/// Holds the latest value for a value watch, retaining at most one committed and two pending values.
/// </summary>
internal sealed class ValueWatcher<T> : StateWatcher<ValueChange<T>>, IChangeSink<T>
{
    private T? _value;
    private long _version;
    private bool _hasUnreadValue;

    // Committed mode: values which await their write's acknowledgement. Writes are acknowledged in order, one
    // capture at a time, so pending values span at most the in-flight version and the next version.
    private T? _pendingValue0;
    private long _pendingVersion0;
    private T? _pendingValue1;
    private long _pendingVersion1;
    private int _pendingCount;

    // Staged mode: the version delivered as staged which awaits confirmation. Zero when none.
    private long _awaitingConfirmation;

    public ValueWatcher(JournaledStateObservers hub, IStateMachine state, DurableWatchMode mode, T? initialValue, long initialVersion)
        : base(hub, state, mode)
    {
        _pendingValue0 = initialValue;
        _pendingVersion0 = initialVersion;
    }

    protected override void OnAttached()
    {
        var initialValue = _pendingValue0;
        var initialVersion = _pendingVersion0;
        _pendingValue0 = default;
        OnChange(initialValue!, initialVersion);
    }

    public void OnChange(T value, long version)
    {
        if (IncludesStaged || version <= CommittedVersion)
        {
            // Durable values supersede values which await acknowledgement.
            _pendingCount = 0;
            (_pendingValue0, _pendingValue1) = (default, default);
            SetLatest(value, version);
            return;
        }

        if (_pendingCount > 0 && (_pendingCount == 1 ? _pendingVersion0 : _pendingVersion1) == version)
        {
            if (_pendingCount == 1)
            {
                _pendingValue0 = value;
            }
            else
            {
                _pendingValue1 = value;
            }

            return;
        }

        Debug.Assert(_pendingCount < 2, "Pending values span at most two write versions.");
        if (_pendingCount == 0)
        {
            (_pendingValue0, _pendingVersion0) = (value, version);
        }
        else
        {
            (_pendingValue1, _pendingVersion1) = (value, version);
        }

        _pendingCount++;
    }

    internal override void OnWatermark(long version)
    {
        CommittedVersion = version;
        if (!IncludesStaged)
        {
            while (_pendingCount > 0 && _pendingVersion0 <= version)
            {
                SetLatest(_pendingValue0!, _pendingVersion0);
                (_pendingValue0, _pendingVersion0) = (_pendingValue1, _pendingVersion1);
                (_pendingValue1, _pendingVersion1) = (default, 0);
                _pendingCount--;
            }
        }
        else if (_awaitingConfirmation != 0 && _awaitingConfirmation <= version)
        {
            // Deliver the staged value again as committed.
            _hasUnreadValue = true;
            _awaitingConfirmation = 0;
            Signal();
        }
    }

    public override bool TryRead(out ValueChange<T> change)
    {
        Hub.AssertConfined();
        if (!_hasUnreadValue)
        {
            change = default;
            return false;
        }

        _hasUnreadValue = false;
        var status = _version <= CommittedVersion ? DurableChangeStatus.Committed : DurableChangeStatus.Staged;
        if (IncludesStaged)
        {
            _awaitingConfirmation = status is DurableChangeStatus.Staged ? _version : 0;
        }

        change = new() { Value = _value, Status = status, Version = _version };
        return true;
    }

    private void SetLatest(T value, long version)
    {
        _value = value;
        _version = version;
        _hasUnreadValue = true;
        _awaitingConfirmation = 0;
        Signal();
    }
}

/// <summary>
/// Connects durable state implementations to their manager's observer hub.
/// </summary>
internal static class DurableStateObservation
{
    /// <summary>
    /// The number of buffered changes each collection watcher retains before it resynchronizes.
    /// </summary>
    public const int DefaultBufferCapacity = 1024;

    /// <summary>
    /// Gets the watchers of <paramref name="state"/>. Returns <see langword="false"/> without allocating when it is unwatched.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetTopic(JournaledStateManager? manager, IStateMachine state, [NotNullWhen(true)] out StateTopic? topic)
    {
        if (manager?.Observers is { } observers)
        {
            return observers.TryGetTopic(state, out topic);
        }

        topic = null;
        return false;
    }

    public static JournaledStateManager GetRequiredManager(JournaledStateManager? manager) =>
        manager ?? throw new NotSupportedException("Watching requires state registered with the built-in journaled state manager.");

    /// <summary>
    /// Publishes a snapshot of durable contents, such as recovered or reset state, if <paramref name="state"/> is watched.
    /// </summary>
    public static void PublishDurableResync<TState, TChange>(JournaledStateManager? manager, TState state)
        where TState : IStateMachine, IResyncSource<TChange>
    {
        if (TryGetTopic(manager, state, out var topic))
        {
            topic.PublishDurable(state.CreateResync());
        }
    }

    /// <summary>
    /// Publishes a snapshot of staged contents, such as the result of a bulk mutation, if <paramref name="state"/> is watched.
    /// </summary>
    public static void PublishStagedResync<TState, TChange>(JournaledStateManager? manager, TState state)
        where TState : IStateMachine, IResyncSource<TChange>
    {
        if (TryGetTopic(manager, state, out var topic))
        {
            topic.PublishStaged(state.CreateResync());
        }
    }

    public static void ValidateMode(DurableWatchMode mode)
    {
        if ((mode & ~(DurableWatchMode.Staged | DurableWatchMode.Drafts)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The watch mode is not supported.");
        }
    }

    public static IAsyncEnumerable<TChange> WatchChanges<TState, TChange>(
        JournaledStateManager? manager,
        TState state,
        DurableWatchMode mode,
        int capacity,
        CancellationToken cancellationToken)
        where TState : IStateMachine, IResyncSource<TChange>
        where TChange : struct, IDurableChange<TChange>
    {
        ValidateMode(mode);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        return WatchAsync(
            GetRequiredManager(manager),
            (state, mode, capacity),
            static (hub, arg) =>
            {
                // Staged entries are captured by the next write. Otherwise, the snapshot is durable once the latest captured write is acknowledged.
                var (state, mode, capacity) = arg;
                var manager = hub.Manager;
                var initialVersion = state.HasStagedEntries ? manager.StagingWriteVersion : manager.CapturedWriteVersion;
                return new ChangeWatcher<TChange>(hub, state, state, mode, capacity, state.CreateResync(), initialVersion);
            },
            cancellationToken);
    }

    public static IAsyncEnumerable<ValueChange<T>> WatchValue<T>(
        JournaledStateManager? manager,
        DurableValue<T> state,
        DurableWatchMode mode,
        CancellationToken cancellationToken)
    {
        ValidateMode(mode);
        return WatchAsync(
            GetRequiredManager(manager),
            (state, mode),
            static (hub, arg) =>
            {
                // A dirty value is captured by the next write. Otherwise, the value is durable once the latest captured write is acknowledged.
                var (state, mode) = arg;
                var manager = hub.Manager;
                var initialVersion = state.IsDirty ? manager.StagingWriteVersion : manager.CapturedWriteVersion;
                return new ValueWatcher<T>(hub, state, mode, state.LiveValue, initialVersion);
            },
            cancellationToken);
    }

    /// <summary>
    /// Creates a watcher when enumeration begins and delivers its events until the hub completes or the token is canceled.
    /// </summary>
    public static async IAsyncEnumerable<TEvent> WatchAsync<TArg, TEvent>(
        JournaledStateManager manager,
        TArg arg,
        Func<JournaledStateObservers, TArg, StateWatcher<TEvent>> createWatcher,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hub = manager.GetOrCreateObservers();
        var watcher = createWatcher(hub, arg);
        hub.Add(watcher);
        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state => ((StateWatcher)state!).Signal(), watcher)
            : default;
        try
        {
            while (true)
            {
                while (watcher.TryRead(out var value))
                {
                    yield return value;
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (watcher.CheckCompleted())
                {
                    yield break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await watcher.WaitAsync();
            }
        }
        finally
        {
            hub.Remove(watcher);
        }
    }
}
