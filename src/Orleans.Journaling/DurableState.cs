using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;

namespace Orleans.Journaling;

[DebuggerDisplay("{Value}")]
internal sealed class DurableState<T> : IPersistentState<T>, IJournaledState, IPersistentStateCommandHandler<T>
{
    private readonly IPersistentStateCommandCodec<T> _codec;
    private readonly IJournaledStateManager _manager;
    private T? _value;
    private ulong _version;
    private ulong _pendingVersion;
    private PendingWriteKind _pendingWrite;
    private bool _hasState;
    private bool _clearRequested;
    private bool _isDirty;
    private ulong _changeVersion;
    private ulong _stagedChangeVersion;

    public DurableState(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IPersistentStateCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        manager.RegisterState(key, this);
        _manager = manager;
    }

    internal DurableState(string key, IJournaledStateManager manager, IPersistentStateCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterState(key, this);
        _manager = manager;
    }

    public Action? OnPersisted { get; set; }
    T IStorage<T>.State
    {
        get
        {
            _hasState = true;
            _isDirty = true;
            _changeVersion++;
            return _value ??= Activator.CreateInstance<T>();
        }
        set
        {
            _value = value;
            _hasState = true;
            _clearRequested = false;
            _isDirty = true;
            _changeVersion++;
        }
    }

    string IStorage.Etag => $"{_version}";
    bool IStorage.RecordExists => _version > 0;

    bool IJournaledState.HasPendingChanges => _clearRequested || _isDirty;

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IJournaledState.OnWriteCompleted()
    {
        switch (_pendingWrite)
        {
            case PendingWriteKind.Set:
                _version = _pendingVersion;
                if (_stagedChangeVersion == _changeVersion)
                {
                    _clearRequested = false;
                    _hasState = true;
                }
                break;
            case PendingWriteKind.Clear:
                _version = 0;
                if (_stagedChangeVersion == _changeVersion)
                {
                    _clearRequested = false;
                    _hasState = false;
                }
                break;
        }

        _pendingWrite = PendingWriteKind.None;
        _pendingVersion = 0;
        if (_stagedChangeVersion == _changeVersion)
        {
            _isDirty = false;
        }

        OnPersisted?.Invoke();
    }

    void IJournaledState.Reset(JournalStreamWriter writer)
    {
        _value = default;
        _version = 0;
        _pendingVersion = 0;
        _pendingWrite = PendingWriteKind.None;
        _hasState = false;
        _clearRequested = false;
        _isDirty = false;
        _changeVersion = 0;
        _stagedChangeVersion = 0;
    }

    void IJournaledState.AppendEntries(JournalStreamWriter writer)
    {
        if (_clearRequested)
        {
            WriteClear(writer);
            _stagedChangeVersion = _changeVersion;
            _clearRequested = false;
            _isDirty = false;
        }
        else if (_hasState && _isDirty)
        {
            WriteState(writer);
            _stagedChangeVersion = _changeVersion;
            _isDirty = false;
        }
    }

    void IJournaledState.AppendSnapshot(JournalStreamWriter snapshotWriter)
    {
        if (_clearRequested)
        {
            _pendingWrite = PendingWriteKind.Clear;
            _pendingVersion = 0;
            _stagedChangeVersion = _changeVersion;
        }
        else if (_hasState)
        {
            WriteState(snapshotWriter);
            _stagedChangeVersion = _changeVersion;
        }
    }

    public IJournaledState DeepCopy() => throw new NotImplementedException();

    private void WriteState(JournalStreamWriter writer)
    {
        var version = _pendingWrite switch
        {
            PendingWriteKind.Set => _pendingVersion + 1,
            PendingWriteKind.Clear => 1UL,
            _ => _version + 1,
        };
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
        _isDirty = false;
        _changeVersion = 0;
        _stagedChangeVersion = 0;
    }

    void IPersistentStateCommandHandler<T>.ApplyClear()
    {
        _value = default;
        _version = 0;
        _hasState = false;
        _clearRequested = false;
        _isDirty = false;
        _changeVersion = 0;
        _stagedChangeVersion = 0;
    }

    Task IStorage.ClearStateAsync() => ((IStorage)this).ClearStateAsync(CancellationToken.None);
    async Task IStorage.ClearStateAsync(CancellationToken cancellationToken)
    {
        _value = default;
        _hasState = false;
        _clearRequested = true;
        _isDirty = true;
        _changeVersion++;
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
