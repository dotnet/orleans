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
internal sealed class DurableValue<T> : IDurableValue<T>, IStateMachine, IReportsStagedChanges, IDurableValueCommandHandler<T>, IReplicableState, IJournaledStateReplica
{
    private readonly IDurableValueCommandCodec<T> _codec;
    private readonly JournaledStateManager? _manager;
    private readonly JournaledStateAccessTarget _accessTarget;
    private T? _value;
    private bool _isDirty;
    private JournalStreamWriter _writer;

    // Whether the write in flight captured the value. A completed write acknowledges only the value it captured.
    private bool _isValueCaptured;

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
        PublishCurrentValue();
    }

    private void PublishCurrentValue()
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.PublishDurable(_value);
        }
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        if (_isDirty)
        {
            WriteState(writer);
            _isDirty = false;
            _isValueCaptured = true;
        }
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter snapshotWriter)
    {
        WriteState(snapshotWriter);
        _isDirty = false;
        _isValueCaptured = true;
    }


    private void WriteState(JournalStreamWriter writer)
    {
        _codec.WriteSet(_value!, writer);
    }

    void IDurableValueCommandHandler<T>.ApplySet(T value) => ApplySet(value);

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

    // A set journals the value at the next write; a positional read journals it now, so the journal holds what it read.
    void IReplicableState.StagePendingChanges()
    {
        if (_isDirty)
        {
            Debug.Assert(_writer.IsInitialized);
            WriteState(_writer);
            _isDirty = false;
            _isValueCaptured = true;
        }
    }

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
