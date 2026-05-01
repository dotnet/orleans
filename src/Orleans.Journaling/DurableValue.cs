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
    private T? _value;
    private bool _isDirty;

    public DurableValue(
        [ServiceKey] string key,
        ILogManager manager,
        [FromKeyedServices(LogFormatServices.LogFormatKeyServiceKey)] string logFormatKey,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = LogFormatServices.GetRequiredKeyedService<IDurableValueOperationCodecProvider>(serviceProvider, logFormatKey).GetCodec<T>();
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

    object IDurableStateMachine.OperationCodec => _codec;

    void IDurableStateMachine.OnRecoveryCompleted() => OnValuePersisted();
    void IDurableStateMachine.OnWriteCompleted() => OnValuePersisted();

    void IDurableStateMachine.Reset(LogWriter storage)
    {
        _value = default;
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
        _codec.WriteSet(_value!, writer);
    }

    void IDurableValueOperationHandler<T>.ApplySet(T value) => _value = value;
}
