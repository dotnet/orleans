using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public interface IDurableValue<T>
{
    T? Value { get; set; }
}

[DebuggerDisplay("{Value}")]
internal sealed class DurableValue<T> : IDurableValue<T>, IDurableStateMachine, IDurableValueOperationHandler<T>
{
    private readonly IDurableValueOperationCodec<T> _codec;
    private ILogWriter? _storage;
    private T? _value;
    private bool _isDirty;

    public DurableValue([ServiceKey] string key, ILogManager manager, ILogStorage storage, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = LogFormatServices.GetRequiredKeyedService<IDurableValueOperationCodecProvider>(serviceProvider, storage).GetCodec<T>();
        manager.RegisterStateMachine(key, this);
    }

    internal DurableValue(string key, ILogManager manager, IDurableValueOperationCodec<T> codec)
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

    void IDurableStateMachine.OnRecoveryCompleted() => OnValuePersisted();
    void IDurableStateMachine.OnWriteCompleted() => OnValuePersisted();

    void IDurableStateMachine.Reset(ILogWriter storage)
    {
        _value = default;
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(LogWriter logWriter)
    {
        if (_isDirty)
        {
            WriteState(logWriter);
            _isDirty = false;
        }
    }

    void IDurableStateMachine.AppendSnapshot(LogWriter snapshotWriter) => WriteState(snapshotWriter);

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();

    private void WriteState(LogWriter writer)
    {
        using var entry = writer.BeginEntry();
        _codec.WriteSet(_value!, entry.Writer);
        entry.Commit();
    }

    void IDurableValueOperationHandler<T>.ApplySet(T value) => _value = value;

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private ILogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
        return _storage;
    }
}
