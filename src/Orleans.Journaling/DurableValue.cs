using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public interface IDurableValue<T>
{
    T? Value { get; set; }
}

[DebuggerDisplay("{Value}")]
internal sealed class DurableValue<T> : IDurableValue<T>, IJournaledState, IDurableValueOperationHandler<T>
{
    private readonly IDurableValueOperationCodec<T> _codec;
    private readonly IServiceProvider? _serviceProvider;
    private readonly string? _journalFormatKey;
    private T? _value;
    private bool _isDirty;

    public DurableValue(
        [ServiceKey] string key,
        IStateManager manager,
        [FromKeyedServices(JournalFormatServices.JournalFormatKeyServiceKey)] string journalFormatKey,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredKeyedService<IDurableValueOperationCodecProvider>(serviceProvider, journalFormatKey).GetCodec<T>();
        _serviceProvider = serviceProvider;
        _journalFormatKey = journalFormatKey;
        manager.RegisterState(key, this);
    }

    internal DurableValue(string key, IStateManager manager, IDurableValueOperationCodec<T> codec)
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

    private void OnValuePersisted() => OnPersisted?.Invoke();

    public void OnModified() => _isDirty = true;

    object IJournaledState.OperationCodec => _codec;

    object IJournaledState.GetOperationCodec(string journalFormatKey)
    {
        if (_journalFormatKey is null || string.Equals(journalFormatKey, _journalFormatKey, StringComparison.Ordinal))
        {
            return _codec;
        }

        return JournalFormatServices.GetRequiredKeyedService<IDurableValueOperationCodecProvider>(GetServiceProvider(journalFormatKey), journalFormatKey).GetCodec<T>();
    }

    void IJournaledState.OnRecoveryCompleted() => OnValuePersisted();
    void IJournaledState.OnWriteCompleted() => OnValuePersisted();

    void IJournaledState.Reset(JournalStreamWriter writer)
    {
        _value = default;
        _isDirty = false;
    }

    void IJournaledState.AppendEntries(JournalStreamWriter writer)
    {
        if (_isDirty)
        {
            WriteState(writer);
            _isDirty = false;
        }
    }

    void IJournaledState.AppendSnapshot(JournalStreamWriter snapshotWriter) => WriteState(snapshotWriter);

    public IJournaledState DeepCopy() => throw new NotImplementedException();

    private void WriteState(JournalStreamWriter writer)
    {
        _codec.WriteSet(_value!, writer);
    }

    private IServiceProvider GetServiceProvider(string journalFormatKey)
        => _serviceProvider ?? throw new InvalidOperationException(
            $"State '{GetType().FullName}' cannot recover journal format key '{journalFormatKey}' because it was constructed with an explicit operation codec instead of a service provider.");

    void IDurableValueOperationHandler<T>.ApplySet(T value) => _value = value;
}
