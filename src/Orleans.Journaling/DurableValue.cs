using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// Represents a value whose changes are recorded in a journal.
/// </summary>
/// <typeparam name="T">The type of the value.</typeparam>
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
    private T? _value;
    private bool _isDirty;

    public DurableValue(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IDurableValueCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        manager.RegisterStateMachine(key, this);
    }

    internal DurableValue(string key, IJournaledStateManager manager, IDurableValueCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterStateMachine(key, this);
    }

    public T? Value
    {
        get => _value;
        set
        {
            _value = value;
            OnModified();
        }
    }

    public Action? OnPersisted { get; set; }

    private void OnValuePersisted() => OnPersisted?.Invoke();

    public void OnModified() => _isDirty = true;

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.OnRecoveryCompleted() => OnValuePersisted();
    void IStateMachine.OnWriteCompleted() => OnValuePersisted();

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _value = default;
        _isDirty = false;
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        if (_isDirty)
        {
            WriteState(writer);
            _isDirty = false;
        }
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter snapshotWriter) => WriteState(snapshotWriter);


    private void WriteState(JournalStreamWriter writer)
    {
        _codec.WriteSet(_value!, writer);
    }

    void IDurableValueCommandHandler<T>.ApplySet(T value) => _value = value;
}
