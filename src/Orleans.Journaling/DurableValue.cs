using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Orleans.Journaling;

/// <summary>
/// Represents a value whose changes are recorded in a journal.
/// </summary>
/// <typeparam name="T">The type of the value.</typeparam>
/// <remarks>
/// The value takes ownership of each value assigned to it, and the journal records the value when it is assigned. To change the value, assign a new instance.
/// </remarks>
public interface IDurableValue<T>
{
    /// <summary>
    /// Gets or sets the current value.
    /// </summary>
    T? Value { get; set; }
}

[DebuggerDisplay("{Value}")]
internal sealed class DurableValue<T> : IDurableValue<T>, IStateMachine, IReportsStagedChanges, IDurableValueCommandHandler<T>, IReplicableState, IJournaledStateReplica, IJournaledStateResync, ICoalescingJournaledState
{
    private readonly IDurableValueCommandCodec<T> _codec;
    private readonly JournaledStateManager? _manager;
    private readonly JournaledStateAccessTarget _accessTarget;
    private T? _value;
    private bool _isDirty;
    private JournalStreamWriter _writer;

    // Whether the write in flight captured the value. A completed write acknowledges only the value it captured.
    private bool _isValueCaptured;

    // Whether the journal buffer holds the value in an entry which no write captured with this state: a transaction staged
    // it, or an aborted transaction's rebuild replayed it from the unwritten buffer. The next write which captures this
    // state captures that entry, and acknowledges the value.
    private bool _isValueStaged;

    public DurableValue(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IDurableValueCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        _manager = manager as JournaledStateManager;
        _accessTarget = new(key, manager, this);
        manager.RegisterStateMachine(key, this);
    }

    internal DurableValue(string key, IJournaledStateManager manager, IDurableValueCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _manager = manager as JournaledStateManager;
        _accessTarget = new(key, manager, this);
        manager.RegisterStateMachine(key, this);
    }

    public T? Value
    {
        get => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
            ? context.ReadWhole(_accessTarget, JournaledStateOperation.ValueGet, this, static self => self._value)
            : _value;
        set
        {
            if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
            {
                context.Write(_accessTarget, JournaledStateOperation.ValueSet, value, (Self: this, Value: value), static state => state.Self.SetValue(state.Value));
                return;
            }

            SetValue(value);
        }
    }

    private void SetValue(T? value)
    {
        ApplySet(value);
        OnModified();
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.PublishStaged(value);
        }
    }

    internal bool IsDirty => _isDirty;

    /// <summary>Gets the value without running through a context, for observations which report it live.</summary>
    internal T? LiveValue => _value;

    public Action? OnPersisted { get; set; }

    private void OnValuePersisted() => OnPersisted?.Invoke();

    public void OnModified()
    {
        _isDirty = true;
        _writer.MarkModified();
    }

    internal IAsyncEnumerable<ValueChange<T>> WatchAsync(DurableWatchMode mode, CancellationToken cancellationToken) =>
        DurableStateObservation.WatchValue(_manager, this, mode, cancellationToken);

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.OnRecoveryCompleted()
    {
        // Storage holds every entry replayed so far.
        _isValueStaged = false;
        OnValuePersisted();
        PublishCurrentValue();
    }

    void IStateMachine.OnWriteCompleted()
    {
        if (_isValueCaptured)
        {
            _isValueCaptured = false;
            OnValuePersisted();
        }
    }

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _writer = writer;
        ApplySet(default);
        _isDirty = false;
        _isValueCaptured = false;
        _isValueStaged = false;
        PublishCurrentValue();
    }

    private void PublishCurrentValue()
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.PublishDurable(_value);
        }
    }

    void IJournaledStateResync.PublishResync(StateTopic topic) => topic.PublishStaged(_value);

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        Debug.Assert(_manager is not { } manager || !manager.IsHeldByTransaction(this), "A write captures no state which an open transaction holds.");
        if (_isDirty)
        {
            WriteState(writer);
            _isDirty = false;
            _isValueCaptured = true;
        }
        else if (_isValueStaged)
        {
            _isValueCaptured = true;
        }

        _isValueStaged = false;
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter snapshotWriter)
    {
        WriteState(snapshotWriter);
        _isDirty = false;
        _isValueCaptured = true;
        _isValueStaged = false;
    }

    // A transaction journals the value's pending change through its stream: into the journal when it first writes the
    // value, and into its segment when it commits. The write which captures this state after the transaction ends
    // acknowledges it.
    void ICoalescingJournaledState.StagePendingChanges(JournalStreamWriter writer)
    {
        if (_isDirty)
        {
            WriteState(writer);
            _isDirty = false;
            _isValueStaged = true;
        }
    }


    private void WriteState(JournalStreamWriter writer)
    {
        _codec.WriteSet(_value!, writer);
    }

    // A replayed entry which no recovery confirms as stored, such as one which an aborted transaction's rebuild replays from
    // the unwritten journal buffer, awaits the write which captures it.
    void IDurableValueCommandHandler<T>.ApplySet(T value)
    {
        ApplySet(value);
        _isValueStaged = true;
    }

    // Makes every change of the value, for live operations and replayed entries alike, so it records each change.
    private void ApplySet(T? value)
    {
        _accessTarget.OnChanged();
        _value = value;
    }

    // A replica, which has no manager, so its reads run directly.
    private DurableValue(IDurableValueCommandCodec<T> codec)
    {
        _codec = codec;
        _accessTarget = new(nameof(DurableValue<T>));
    }

    // A set journals the value at the next write, so until then the journal doesn't hold the value which reads observe.
    bool IReplicableState.IsReproducibleFromJournal => !_isDirty;

    IJournaledStateReplica IReplicableState.CreateReplica() => new DurableValue<T>(_codec);

    void IJournaledStateReplica.Evaluate(JournaledStateOperation operation, ReadOnlyMemory<byte> argument, Serializer serializer, IBufferWriter<byte> result)
    {
        if (operation is not JournaledStateOperation.ValueGet)
        {
            throw JournaledStateReplicaEncoding.NotPositional(operation, nameof(DurableValue<T>));
        }

        JournaledStateReplicaEncoding.WriteResult(serializer, _value, result);
    }
}
