using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;

namespace Orleans.Journaling;

[DebuggerDisplay("{Value}")]
internal sealed class JournaledPersistentState<T> : IPersistentState<T>, IStateMachine, IPersistentStateCommandHandler<T>
{
    private readonly IPersistentStateCommandCodec<T> _codec;
    private readonly IJournaledStateManager _manager;
    private T? _value;
    private ulong _version;
    private ulong _pendingVersion;
    private PendingWriteKind _pendingWrite;
    private bool _hasState;
    private bool _clearRequested;

    public JournaledPersistentState(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IPersistentStateCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        _manager = manager;
        manager.RegisterStateMachine(key, this);
    }

    internal JournaledPersistentState(string key, IJournaledStateManager manager, IPersistentStateCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _manager = manager;
        manager.RegisterStateMachine(key, this);
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

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.OnWriteCompleted()
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

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _value = default;
        _version = 0;
        _pendingVersion = 0;
        _pendingWrite = PendingWriteKind.None;
        _hasState = false;
        _clearRequested = false;
    }

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
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

    void IStateMachine.WriteSnapshot(JournalStreamWriter snapshotWriter)
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


    private void WriteState(JournalStreamWriter writer)
    {
        var version = _version + 1;
        _codec.WriteSet(_value!, version, writer);
        _pendingWrite = PendingWriteKind.Set;
        _pendingVersion = version;
    }

    private void WriteClear(JournalStreamWriter writer)
    {
        _codec.WriteClear(writer);
        _pendingWrite = PendingWriteKind.Clear;
        _pendingVersion = 0;
    }

    void IPersistentStateCommandHandler<T>.ApplySet(T state, ulong version)
    {
        _value = state;
        _version = version;
        _hasState = true;
        _clearRequested = false;
    }

    void IPersistentStateCommandHandler<T>.ApplyClear()
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
