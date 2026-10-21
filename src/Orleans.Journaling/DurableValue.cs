using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

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
internal sealed class DurableValue<T> : IDurableValue<T>, IStateMachine, IDurableValueCommandHandler<T>
{
    private readonly IDurableValueCommandCodec<T> _codec;
    private readonly JournaledStateManager? _manager;
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
        manager.RegisterStateMachine(key, this);
    }

    internal DurableValue(string key, IJournaledStateManager manager, IDurableValueCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _manager = manager as JournaledStateManager;
        manager.RegisterStateMachine(key, this);
    }

    public T? Value
    {
        get => _value;
        set
        {
            _value = value;
            OnModified();
            if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
            {
                topic.PublishStaged(value);
            }
        }
    }

    internal bool IsDirty => _isDirty;

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
        _value = default;
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

    void IDurableValueCommandHandler<T>.ApplySet(T value) => _value = value;
}
