using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.Durable.Journaling;

/// <summary>
/// Delivers committed changes from one journaled state manager to its watchers.
/// </summary>
/// <remarks>
/// <para>
/// A manager creates at most one hub, when its state is first watched. Watchers are keyed by state machine
/// instance, so state which nobody watches publishes nothing. Each change is tagged with the version of the
/// journal write which will capture it. Each acknowledged write publishes one watermark which commits every
/// change tagged with an equal or lower version, and watchers deliver only committed changes.
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
    /// Delivers a change which the next journal write captures, once that write is acknowledged.
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
    /// Wakes the watchers which read the state directly and deliver volatile draft content.
    /// </summary>
    public void SignalDraftReaders()
    {
        hub.AssertConfined();
        foreach (var watcher in _watchers)
        {
            if (watcher.IncludesDrafts)
            {
                watcher.Signal();
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
/// Receives changes published to a <see cref="StateTopic"/>.
/// </summary>
internal interface IChangeSink<TChange>
{
    void OnChange(TChange change, long version);
}

/// <summary>
/// One reader of a watched state machine.
/// </summary>
/// <remarks>Members other than <see cref="Signal"/> are confined to the hub's scheduler.</remarks>
internal abstract class StateWatcher(JournaledStateObservers hub, IStateMachine state)
{
    private readonly SingleWaiterAutoResetEvent _signal = new() { RunContinuationsAsynchronously = true };
    private Exception? _failure;
    private bool _isCompleted;

    public JournaledStateObservers Hub { get; } = hub;

    public IStateMachine State { get; } = state;

    /// <summary>
    /// Gets a value indicating whether the watcher delivers volatile draft content.
    /// </summary>
    public virtual bool IncludesDrafts => false;

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
internal abstract class StateWatcher<TEvent>(JournaledStateObservers hub, IStateMachine state) : StateWatcher(hub, state)
{
    /// <summary>
    /// Reads the next deliverable event.
    /// </summary>
    public abstract bool TryRead(out TEvent value);
}

/// <summary>
/// Holds the latest committed value for a value watch, retaining at most two values which await acknowledgement.
/// </summary>
internal sealed class ValueWatcher<T> : StateWatcher<ValueChange<T>>, IChangeSink<T>
{
    private T? _value;
    private long _version;
    private bool _hasUnreadValue;

    // Values which await their write's acknowledgement. Writes are acknowledged in order, one capture at a time, so
    // pending values span at most the in-flight version and the next version.
    private T? _pendingValue0;
    private long _pendingVersion0;
    private T? _pendingValue1;
    private long _pendingVersion1;
    private int _pendingCount;

    public ValueWatcher(JournaledStateObservers hub, IStateMachine state, T? initialValue, long initialVersion)
        : base(hub, state)
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
        if (version <= CommittedVersion)
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
        while (_pendingCount > 0 && _pendingVersion0 <= version)
        {
            SetLatest(_pendingValue0!, _pendingVersion0);
            (_pendingValue0, _pendingVersion0) = (_pendingValue1, _pendingVersion1);
            (_pendingValue1, _pendingVersion1) = (default, 0);
            _pendingCount--;
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

        Debug.Assert(_version <= CommittedVersion, "Value watches deliver only committed values.");
        _hasUnreadValue = false;
        change = new() { Value = _value, Version = _version };
        return true;
    }

    private void SetLatest(T value, long version)
    {
        _value = value;
        _version = version;
        _hasUnreadValue = true;
        Signal();
    }
}

/// <summary>
/// Connects durable state implementations to their manager's observer hub.
/// </summary>
internal static class DurableStateObservation
{
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

    public static IAsyncEnumerable<ValueChange<T>> WatchValue<T>(
        JournaledStateManager? manager,
        DurableValue<T> state,
        CancellationToken cancellationToken) =>
        WatchAsync(
            GetRequiredManager(manager),
            state,
            static (hub, state) =>
            {
                // A dirty value is captured by the next write, and a value which the in-flight write captured is durable once that
                // write is acknowledged. Otherwise, the value is already durable, whether or not an unrelated write is in flight.
                var manager = hub.Manager;
                var initialVersion = state.IsDirty
                    ? manager.StagingWriteVersion
                    : state.IsValueCaptured ? manager.CapturedWriteVersion : manager.CommittedWriteVersion;
                return new ValueWatcher<T>(hub, state, state.LiveValue, initialVersion);
            },
            cancellationToken);

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
