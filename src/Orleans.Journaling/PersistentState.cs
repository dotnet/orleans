using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

[DebuggerDisplay("{Value}")]
internal sealed class PersistentState<T> : IPersistentState<T>, IDurableStateMachine
{
    private const byte VersionByte = 0;
    private readonly SerializerSessionPool _serializerSessionPool;
    private readonly IFieldCodec<T> _codec;
    private readonly IStateMachineManager _manager;
    private T? _value;
    private ulong _version;
    private bool _isDirty;
    private bool _hasRecord;

    public PersistentState([ServiceKey] string key, IStateMachineManager manager, IFieldCodec<T> codec, SerializerSessionPool serializerSessionPool)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _serializerSessionPool = serializerSessionPool;
        manager.RegisterStateMachine(key, this);
        _manager = manager;
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
    T IStorage<T>.State { get => Value!; set => Value = value; }
    string IStorage.Etag => $"{_version}";
    bool IStorage.RecordExists => _hasRecord;

    private void OnValuePersisted()
    {
        _hasRecord = true;
        OnPersisted?.Invoke();
    }

    public void OnModified()
    {
        _isDirty = true;
        ++_version;
    }

    void IDurableStateMachine.OnRecoveryCompleted() => OnValuePersisted();
    void IDurableStateMachine.OnWriteCompleted() => OnValuePersisted();

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage)
    {
        _value = default;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        using var session = _serializerSessionPool.GetSession();
        var reader = Reader.Create(logEntry, session);
        var version = reader.ReadByte();
        if (version != VersionByte)
        {
            throw new NotSupportedException($"This instance of {nameof(DurableValue<T>)} supports version {(uint)VersionByte} and not version {(uint)version}.");
        }

        var commandType = (CommandType)reader.ReadVarUInt32();
        switch (commandType)
        {
            case CommandType.SetValue:
                SetValue(ref reader);
                break;
            default:
                throw new NotSupportedException($"Command type {commandType} is not supported");
        }

        void SetValue(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            _value = _codec.ReadValue(ref reader, field);
            _version = reader.ReadVarUInt64();
            _hasRecord = true;
        }
    }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter)
    {
        if (_isDirty)
        {
            WriteState(logWriter);
            _isDirty = false;
        }
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter) => WriteState(snapshotWriter);

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();

    private void WriteState(StateMachineStorageWriter writer)
    {
        writer.AppendEntry(static (self, bufferWriter) =>
        {
            using var session = self._serializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.SetValue);
            self._codec.WriteField(ref writer, 0, typeof(T), self._value!);
            writer.WriteVarUInt64(self._version);
            writer.Commit();
        }, this);
    }

    Task IStorage.ClearStateAsync() => ((IStorage)this).ClearStateAsync(CancellationToken.None);
    async Task IStorage.ClearStateAsync(CancellationToken cancellationToken)
    {
        Value = default;
        await _manager.WriteStateAsync(cancellationToken);
    }

    Task IStorage.WriteStateAsync() => ((IStorage)this).WriteStateAsync(CancellationToken.None);
    async Task IStorage.WriteStateAsync(CancellationToken cancellationToken)
    {
        await _manager.WriteStateAsync(cancellationToken);
    }

    Task IStorage.ReadStateAsync() => ((IStorage)this).ReadStateAsync(CancellationToken.None);
    Task IStorage.ReadStateAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private enum CommandType
    {
        SetValue,
    }
}
