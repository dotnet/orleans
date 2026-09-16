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
internal sealed class DurableValue<T> : IDurableValue<T>, IJournaledState, IDurableValueCommandHandler<T>
{
    private readonly IDurableValueCommandCodec<T> _codec;
    private T? _value;
    private bool _isDirty;
    private ulong _changeVersion;
    private ulong _stagedChangeVersion;

    public DurableValue(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IDurableValueCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        manager.RegisterState(key, this);
    }

    internal DurableValue(string key, IJournaledStateManager manager, IDurableValueCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterState(key, this);
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

    bool IJournaledState.HasPendingChanges => _isDirty;

    private void OnValuePersisted() => OnPersisted?.Invoke();

    public void OnModified()
    {
        _isDirty = true;
        _changeVersion++;
    }

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IJournaledState.OnRecoveryCompleted()
    {
        _isDirty = false;
        _changeVersion = 0;
        _stagedChangeVersion = 0;
        OnValuePersisted();
    }

    void IJournaledState.OnWriteCompleted()
    {
        if (_stagedChangeVersion == _changeVersion)
        {
            _isDirty = false;
        }

        OnValuePersisted();
    }

    void IJournaledState.Reset(JournalStreamWriter writer)
    {
        _value = default;
        _isDirty = false;
        _changeVersion = 0;
        _stagedChangeVersion = 0;
    }

    void IJournaledState.AppendEntries(JournalStreamWriter writer)
    {
        if (_isDirty)
        {
            WriteState(writer);
            _stagedChangeVersion = _changeVersion;
            _isDirty = false;
        }
    }

    void IJournaledState.AppendSnapshot(JournalStreamWriter snapshotWriter)
    {
        WriteState(snapshotWriter);
        _stagedChangeVersion = _changeVersion;
    }

    public IJournaledState DeepCopy() => throw new NotImplementedException();

    private void WriteState(JournalStreamWriter writer)
    {
        _codec.WriteSet(_value!, writer);
    }

    void IDurableValueCommandHandler<T>.ApplySet(T value)
    {
        _value = value;
        _isDirty = false;
        _changeVersion = 0;
        _stagedChangeVersion = 0;
    }
}
