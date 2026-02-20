using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;

namespace Orleans.Journaling;

[DebuggerDisplay("{Value}")]
internal sealed class DurableState<T> : IPersistentState<T>, IDurableStateMachine
{
    private readonly ILogEntryCodec<DurableStateEntry<T>> _entryCodec;
    private readonly IStateMachineManager _manager;
    private T? _value;
    private ulong _version;

    public DurableState([ServiceKey] string key, IStateMachineManager manager, ILogEntryCodec<DurableStateEntry<T>> entryCodec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _entryCodec = entryCodec;
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

    void IDurableStateMachine.OnWriteCompleted()
    {
        _version++;
        OnPersisted?.Invoke();
    }

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage) => _value = default;

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        var entry = _entryCodec.Read(logEntry);
        switch (entry)
        {
            case StateClearEntry<T>:
                _value = default;
                _version = 0;
                break;
            case StateSetEntry<T>(var state, var version):
                _value = state;
                _version = version;
                break;
        }
    }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter) => WriteState(logWriter);

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter) => WriteState(snapshotWriter);

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();

    private void WriteState(StateMachineStorageWriter writer)
    {
        writer.AppendEntry(static (self, bufferWriter) =>
        {
            self._entryCodec.Write(new StateSetEntry<T>(self._value!, self._version), bufferWriter);
        }, this);
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
