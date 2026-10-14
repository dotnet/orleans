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
/// Lock order: the manager lock precedes <see cref="SyncRoot"/>. The hub never acquires the manager lock.
/// </para>
/// </remarks>
internal sealed class JournaledStateObservers
{
    private Dictionary<IStateMachine, StateTopic>? _topics;
    private long _committedVersion;
    private Exception? _failure;
    private bool _isCompleted;

    public JournaledStateObservers(JournaledStateManager manager, long committedVersion)
    {
        Manager = manager;
        _committedVersion = committedVersion;
    }

#if NET9_0_OR_GREATER
    public Lock SyncRoot { get; } = new();
#else
    public object SyncRoot { get; } = new();
#endif

    public JournaledStateManager Manager { get; }

    /// <summary>
    /// Gets the watchers of <paramref name="state"/>, if it is watched.
    /// </summary>
    /// <remarks>Readers use the published copy-on-write topic map without locking.</remarks>
    public bool TryGetTopic(IStateMachine state, [NotNullWhen(true)] out StateTopic? topic)
    {
        if (Volatile.Read(ref _topics) is { } topics)
        {
            return topics.TryGetValue(state, out topic);
        }

        topic = null;
        return false;
    }

    public void Add(StateWatcher watcher)
    {
        lock (SyncRoot)
        {
            if (_isCompleted)
            {
                watcher.Attach(_committedVersion);
                watcher.Complete(_failure);
                return;
            }

            var topics = _topics;
            if (topics is null || !topics.TryGetValue(watcher.State, out var topic))
            {
                topic = new StateTopic(this);
                topics = topics is null
                    ? new Dictionary<IStateMachine, StateTopic>(ReferenceEqualityComparer.Instance)
                    : new Dictionary<IStateMachine, StateTopic>(topics, ReferenceEqualityComparer.Instance);
                topics.Add(watcher.State, topic);
                Volatile.Write(ref _topics, topics);
            }

            topic.Add(watcher);
            watcher.Attach(_committedVersion);
        }
    }

    public void Remove(StateWatcher watcher)
    {
        lock (SyncRoot)
        {
            var topics = _topics;
            if (topics is null || !topics.TryGetValue(watcher.State, out var topic) || !topic.Remove(watcher) || !topic.IsEmpty)
            {
                return;
            }

            if (topics.Count == 1)
            {
                Volatile.Write(ref _topics, null);
                return;
            }

            var remaining = new Dictionary<IStateMachine, StateTopic>(topics, ReferenceEqualityComparer.Instance);
            remaining.Remove(watcher.State);
            Volatile.Write(ref _topics, remaining);
        }
    }

    public void OnWriteCommitted(long version)
    {
        lock (SyncRoot)
        {
            Debug.Assert(version > _committedVersion);
            _committedVersion = version;
            if (_topics is { } topics)
            {
                foreach (var topic in topics.Values)
                {
                    topic.OnWatermark(version);
                }
            }
        }
    }

    public void Complete(Exception? failure)
    {
        lock (SyncRoot)
        {
            if (_isCompleted)
            {
                return;
            }

            _isCompleted = true;
            _failure = failure;
            if (_topics is { } topics)
            {
                foreach (var topic in topics.Values)
                {
                    topic.Complete(failure);
                }
            }
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
    /// Delivers a change to the watchers.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="isDurable">
    /// Whether the change reflects durable state, such as recovered or deleted state, rather than a change staged for the next write.
    /// </param>
    public void Publish<TChange>(TChange change, bool isDurable = false)
    {
        lock (hub.SyncRoot)
        {
            var version = isDurable ? hub.Manager.CapturedWriteVersion : hub.Manager.StagingWriteVersion;
            foreach (var watcher in _watchers)
            {
                ((IChangeSink<TChange>)watcher).OnChange(change, version);
            }
        }
    }

    /// <summary>
    /// Wakes watchers which read the state directly.
    /// </summary>
    /// <param name="visibility">
    /// The mode flags which make the change observable, or <see cref="DurableWatchMode.Committed"/> to wake every watcher.
    /// </param>
    public void Signal(DurableWatchMode visibility)
    {
        lock (hub.SyncRoot)
        {
            foreach (var watcher in _watchers)
            {
                if (visibility is DurableWatchMode.Committed || (watcher.Mode & visibility) != 0)
                {
                    watcher.Signal();
                }
            }
        }
    }

    internal void Add(StateWatcher watcher) => _watchers.Add(watcher);

    internal bool Remove(StateWatcher watcher) => _watchers.Remove(watcher);

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
/// Receives changes published to a <see cref="StateTopic"/> under the hub lock.
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
/// <remarks>Mutable members are guarded by the hub lock.</remarks>
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

    protected long CommittedVersion { get; private set; }

    public void Signal() => _signal.Signal();

    public ValueTask WaitAsync() => _signal.WaitAsync();

    /// <summary>
    /// Returns <see langword="true"/> when the hub has completed; throws when it completed with a failure.
    /// </summary>
    public bool CheckCompleted()
    {
        lock (Hub.SyncRoot)
        {
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
    }

    internal void Attach(long committedVersion)
    {
        CommittedVersion = committedVersion;
        OnAttached();
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
/// Buffers a bounded number of collection changes and resynchronizes the reader after an overflow.
/// </summary>
internal sealed class ChangeWatcher<TChange> : StateWatcher, IChangeSink<TChange>
    where TChange : struct, IDurableChange<TChange>
{
    private readonly IResyncSource<TChange> _source;
    private readonly Queue<(TChange Change, long Version)> _buffer = new();
    private readonly int _capacity;
    private TChange _initialChange;
    private long _initialVersion;

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
        if (version <= CommittedVersion || _buffer.Count >= _capacity)
        {
            // Durable resyncs replace every buffered change. After an overflow, the snapshot includes every buffered change and this one.
            if (version > CommittedVersion)
            {
                change = _source.CreateResync();
            }

            _buffer.Clear();
        }

        _buffer.Enqueue((change, version));
        if (IncludesStaged || version <= CommittedVersion)
        {
            Signal();
        }
    }

    public bool TryRead(out TChange change)
    {
        lock (Hub.SyncRoot)
        {
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

            if (_unconfirmedVersion != 0 && _unconfirmedVersion <= CommittedVersion)
            {
                change = TChange.CreateWatermark(CommittedVersion);
                _unconfirmedVersion = _lastStagedVersion > CommittedVersion ? CommittedVersion + 1 : 0;
                return true;
            }

            change = default;
            return false;
        }
    }
}

/// <summary>
/// Holds the latest value for a value watch, retaining at most one committed and two pending values.
/// </summary>
internal sealed class ValueWatcher<T> : StateWatcher, IChangeSink<T>
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
        base.OnWatermark(version);
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
        }
    }

    public bool TryRead(out ValueChange<T> change)
    {
        lock (Hub.SyncRoot)
        {
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

    public static void ValidateWatch(DurableWatchMode mode, int capacity)
    {
        if ((mode & ~(DurableWatchMode.Staged | DurableWatchMode.Drafts)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The watch mode is not supported.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
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
        ValidateWatch(mode, capacity);
        return WatchChangesCore<TState, TChange>(GetRequiredManager(manager), state, mode, capacity, cancellationToken);
    }

    public static IAsyncEnumerable<ValueChange<T>> WatchValue<T>(
        JournaledStateManager? manager,
        DurableValue<T> state,
        DurableWatchMode mode,
        CancellationToken cancellationToken)
    {
        ValidateWatch(mode, capacity: 1);
        return WatchValueCore(GetRequiredManager(manager), state, mode, cancellationToken);
    }

    private static async IAsyncEnumerable<TChange> WatchChangesCore<TState, TChange>(
        JournaledStateManager manager,
        TState state,
        DurableWatchMode mode,
        int capacity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TState : IStateMachine, IResyncSource<TChange>
        where TChange : struct, IDurableChange<TChange>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hub = manager.GetOrCreateObservers();

        // Staged entries are captured by the next write. Otherwise, the snapshot is durable once the latest captured write is acknowledged.
        var initialVersion = state.HasStagedEntries ? manager.StagingWriteVersion : manager.CapturedWriteVersion;
        var watcher = new ChangeWatcher<TChange>(hub, state, state, mode, capacity, state.CreateResync(), initialVersion);
        hub.Add(watcher);
        using var registration = RegisterCancellation(watcher, cancellationToken);
        try
        {
            while (true)
            {
                while (watcher.TryRead(out var change))
                {
                    yield return change;
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

    private static async IAsyncEnumerable<ValueChange<T>> WatchValueCore<T>(
        JournaledStateManager manager,
        DurableValue<T> state,
        DurableWatchMode mode,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hub = manager.GetOrCreateObservers();

        // A dirty value is captured by the next write. Otherwise, the value is durable once the latest captured write is acknowledged.
        var initialVersion = state.IsDirty ? manager.StagingWriteVersion : manager.CapturedWriteVersion;
        var watcher = new ValueWatcher<T>(hub, state, mode, state.Value, initialVersion);
        hub.Add(watcher);
        using var registration = RegisterCancellation(watcher, cancellationToken);
        try
        {
            while (true)
            {
                while (watcher.TryRead(out var change))
                {
                    yield return change;
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

    public static CancellationTokenRegistration RegisterCancellation(StateWatcher watcher, CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled
            ? cancellationToken.UnsafeRegister(static state => ((StateWatcher)state!).Signal(), watcher)
            : default;
}
