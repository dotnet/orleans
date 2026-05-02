using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;

namespace Orleans.Journaling;

[DebuggerDisplay("{Value}")]
internal sealed class DurableState<T> : IPersistentState<T>, IDurableStateMachine, IDurableStateOperationHandler<T>
{
    private readonly IDurableStateOperationCodec<T> _codec;
    private readonly IStateMachineManager _manager;
    private T? _value;
    private ulong _version;
    private ulong _pendingVersion;
    private PendingWriteKind _pendingWrite;
    private bool _hasState;
    private bool _clearRequested;

    public DurableState(
        [ServiceKey] string key,
        IStateMachineManager manager,
        [FromKeyedServices(LogFormatServices.LogFormatKeyServiceKey)] string logFormatKey,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = LogFormatServices.GetRequiredKeyedService<IDurableStateOperationCodecProvider>(serviceProvider, logFormatKey).GetCodec<T>();
        manager.RegisterStateMachine(key, this);
        _manager = manager;
    }

    internal DurableState(string key, IStateMachineManager manager, IDurableStateOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterStateMachine(key, this);
        _manager = manager;
    }

    public Action? OnPersisted { get; set; }
    T IStorage<T>.State
    {
        get
        {
            _hasState = true;
            return _value ??= Activator.CreateInstance<T>();
        }
        set
        {
            _value = value;
            _hasState = true;
            _clearRequested = false;
        }
    }

    string IStorage.Etag => $"{_version}";
    bool IStorage.RecordExists => _version > 0;

    object IDurableStateMachine.OperationCodec => _codec;

    void IDurableStateMachine.OnWriteCompleted()
    {
        switch (_pendingWrite)
        {
            case PendingWriteKind.Set:
                _version = _pendingVersion;
                _clearRequested = false;
                _hasState = true;
                break;
            case PendingWriteKind.Clear:
                _version = 0;
                _clearRequested = false;
                _hasState = false;
                break;
        }

        _pendingWrite = PendingWriteKind.None;
        _pendingVersion = 0;
        OnPersisted?.Invoke();
    }

    void IDurableStateMachine.Reset(LogStreamWriter writer)
    {
        _value = default;
        _version = 0;
        _pendingVersion = 0;
        _pendingWrite = PendingWriteKind.None;
        _hasState = false;
        _clearRequested = false;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(LogStreamWriter writer)
    {
        if (_clearRequested)
        {
            WriteClear(writer);
        }
        else if (_hasState)
        {
            WriteState(writer);
        }
    }

    void IDurableStateMachine.AppendSnapshot(LogStreamWriter snapshotWriter)
    {
        if (_clearRequested)
        {
            _pendingWrite = PendingWriteKind.Clear;
            _pendingVersion = 0;
        }
        else if (_hasState)
        {
            WriteState(snapshotWriter);
        }
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();

    private void WriteState(LogStreamWriter writer)
    {
        var version = _version + 1;
        _codec.WriteSet(_value!, version, writer);
        _pendingWrite = PendingWriteKind.Set;
        _pendingVersion = version;
    }

    private void WriteClear(LogStreamWriter writer)
    {
        _codec.WriteClear(writer);
        _pendingWrite = PendingWriteKind.Clear;
        _pendingVersion = 0;
    }

    void IDurableStateOperationHandler<T>.ApplySet(T state, ulong version)
    {
        _value = state;
        _version = version;
        _hasState = true;
        _clearRequested = false;
    }

    void IDurableStateOperationHandler<T>.ApplyClear()
    {
        _value = default;
        _version = 0;
        _hasState = false;
        _clearRequested = false;
    }

    Task IStorage.ClearStateAsync() => ((IStorage)this).ClearStateAsync(CancellationToken.None);
    async Task IStorage.ClearStateAsync(CancellationToken cancellationToken)
    {
        _value = default;
        _hasState = false;
        _clearRequested = true;
        await _manager.WriteStateAsync(cancellationToken);
    }

    Task IStorage.WriteStateAsync() => ((IStorage)this).WriteStateAsync(CancellationToken.None);
    async Task IStorage.WriteStateAsync(CancellationToken cancellationToken) => await _manager.WriteStateAsync(cancellationToken);
    Task IStorage.ReadStateAsync() => ((IStorage)this).ReadStateAsync(CancellationToken.None);
    Task IStorage.ReadStateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private enum PendingWriteKind
    {
        None,
        Set,
        Clear
    }
}
