using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;

namespace Orleans.Journaling;

[DebuggerDisplay("{Value}")]
internal sealed class DurableState<T> : IPersistentState<T>, IDurableStateMachine, IDurableStateOperationHandler<T>
{
    private readonly IDurableStateOperationCodec<T> _codec;
    private readonly ILogManager _manager;
    private T? _value;
    private ulong _version;

    public DurableState([ServiceKey] string key, ILogManager manager, LogFormatKey logFormatKey, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = LogFormatServices.GetRequiredKeyedService<IDurableStateOperationCodecProvider>(serviceProvider, logFormatKey).GetCodec<T>();
        manager.RegisterStateMachine(key, this);
        _manager = manager;
    }

    internal DurableState(string key, ILogManager manager, IDurableStateOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterStateMachine(key, this);
        _manager = manager;
    }

    public Action? OnPersisted { get; set; }
    T IStorage<T>.State
    {
        get => _value ??= Activator.CreateInstance<T>();
        set => _value = value;
    }

    string IStorage.Etag => $"{_version}";
    bool IStorage.RecordExists => _version > 0;

    object IDurableStateMachine.OperationCodec => _codec;

    void IDurableStateMachine.OnWriteCompleted()
    {
        _version++;
        OnPersisted?.Invoke();
    }

    void IDurableStateMachine.Reset(ILogWriter storage) => _value = default;

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(LogWriter logWriter) => WriteState(logWriter);

    void IDurableStateMachine.AppendSnapshot(LogWriter snapshotWriter) => WriteState(snapshotWriter);

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();

    private void WriteState(LogWriter writer)
    {
        using var entry = writer.BeginEntry();
        _codec.WriteSet(_value!, _version, entry.Writer);
        entry.Commit();
    }

    void IDurableStateOperationHandler<T>.ApplySet(T state, ulong version)
    {
        _value = state;
        _version = version;
    }

    void IDurableStateOperationHandler<T>.ApplyClear()
    {
        _value = default;
        _version = 0;
    }

    Task IStorage.ClearStateAsync() => ((IStorage)this).ClearStateAsync(CancellationToken.None);
    async Task IStorage.ClearStateAsync(CancellationToken cancellationToken)
    {
        _value = default;
        _version = 0;
        await _manager.WriteStateAsync(cancellationToken);
    }

    Task IStorage.WriteStateAsync() => ((IStorage)this).WriteStateAsync(CancellationToken.None);
    async Task IStorage.WriteStateAsync(CancellationToken cancellationToken) => await _manager.WriteStateAsync(cancellationToken);
    Task IStorage.ReadStateAsync() => ((IStorage)this).ReadStateAsync(CancellationToken.None);
    Task IStorage.ReadStateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
