using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

public interface IDurableValue<T>
{
    T? Value { get; set; }
}

[DebuggerDisplay("{Value}")]
internal sealed class DurableValue<T> : IDurableValue<T>, IDurableStateMachine
{
    private const byte VersionByte = 0;
    private readonly SerializerSessionPool _serializerSessionPool;
    private readonly IFieldCodec<T> _codec;
    private IStateMachineLogWriter? _storage;
    private T? _value;
    private bool _isDirty;

    public DurableValue([ServiceKey] string key, IStateMachineManager manager, IFieldCodec<T> codec, SerializerSessionPool serializerSessionPool)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _serializerSessionPool = serializerSessionPool;
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

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage)
    {
        _value = default;
        _storage = storage;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        T ReadValue(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _codec.ReadValue(ref reader, field);
        }

        void SetValue(ref Reader<ReadOnlySequenceInput> reader) => _value = ReadValue(ref reader);
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
            writer.Commit();
        }, this);
    }

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private IStateMachineLogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
        return _storage;
    }

    private enum CommandType
    {
        SetValue,
    }
}
