using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

public interface IDurableSet<T> : ISet<T>, IReadOnlyCollection<T>, IReadOnlySet<T>
{
    new int Count { get; }
    new bool Contains(T item);
    new bool Add(T item);
    new bool IsProperSubsetOf(IEnumerable<T> other);
    new bool IsProperSupersetOf(IEnumerable<T> other);
    new bool IsSubsetOf(IEnumerable<T> other);
    new bool IsSupersetOf(IEnumerable<T> other);
    new bool Overlaps(IEnumerable<T> other);
    new bool SetEquals(IEnumerable<T> other);
}

[DebuggerTypeProxy(typeof(IDurableCollectionDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class DurableSet<T> : IDurableSet<T>, IDurableStateMachine
{
    private readonly SerializerSessionPool _serializerSessionPool;
    private readonly IFieldCodec<T> _codec;
    private const byte VersionByte = 0;
    private readonly HashSet<T> _items = [];
    private IStateMachineLogWriter? _storage;

    public DurableSet([ServiceKey] string key, IStateMachineManager manager, IFieldCodec<T> codec, SerializerSessionPool serializerSessionPool)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _serializerSessionPool = serializerSessionPool;
        manager.RegisterStateMachine(key, this);
    }

    public int Count => _items.Count;
    public bool IsReadOnly => false;

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage)
    {
        _items.Clear();
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        using var session = _serializerSessionPool.GetSession();
        var reader = Reader.Create(logEntry, session);
        var version = reader.ReadByte();
        if (version != VersionByte)
        {
            throw new NotSupportedException($"This instance of {nameof(DurableSet<T>)} supports version {(uint)VersionByte} and not version {(uint)version}.");
        }

        var commandType = (CommandType)reader.ReadVarUInt32();
        switch (commandType)
        {
            case CommandType.Add:
                ApplyAdd(ReadValue(ref reader));
                break;
            case CommandType.Remove:
                ApplyRemove(ReadValue(ref reader));
                break;
            case CommandType.Clear:
                ApplyClear();
                break;
            case CommandType.Snapshot:
                ApplySnapshot(ref reader);
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

        void ApplySnapshot(ref Reader<ReadOnlySequenceInput> reader)
        {
            var count = (int)reader.ReadVarUInt32();
            ApplyClear();
            _items.EnsureCapacity(count);
            for (var i = 0; i < count; i++)
            {
                ApplyAdd(ReadValue(ref reader));
            }
        }
    }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter)
    {
        snapshotWriter.AppendEntry(WriteSnapshotToBufferWriter, this);
    }

    private static void WriteSnapshotToBufferWriter(DurableSet<T> self, IBufferWriter<byte> bufferWriter)
    {
        using var session = self._serializerSessionPool.GetSession();
        var writer = Writer.Create(bufferWriter, session);
        writer.WriteByte(VersionByte);
        writer.WriteVarUInt32((uint)CommandType.Snapshot);
        writer.WriteVarUInt32((uint)self._items.Count);
        foreach (var item in self._items)
        {
            self._codec.WriteField(ref writer, 0, typeof(T), item);
        }

        writer.Commit();
    }

    public void Clear()
    {
        ApplyClear();
        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            using var session = state._serializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.Clear);
            writer.Commit();
        },
        this);
    }

    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    public bool Add(T item)
    {
        if (ApplyAdd(item))
        {
            GetStorage().AppendEntry(static (state, bufferWriter) =>
            {
                var (self, item) = state;
                using var session = self._serializerSessionPool.GetSession();
                var writer = Writer.Create(bufferWriter, session);
                writer.WriteByte(VersionByte);
                writer.WriteVarUInt32((uint)CommandType.Add);
                self._codec.WriteField(ref writer, 0, typeof(T), item!);
                writer.Commit();
            },
            (this, item));
            return true;
        }

        return false;
    }

    public bool Remove(T item)
    {
        if (ApplyRemove(item))
        {
            GetStorage().AppendEntry(static (state, bufferWriter) =>
            {
                var (self, item) = state;
                using var session = self._serializerSessionPool.GetSession();
                var writer = Writer.Create(bufferWriter, session);
                writer.WriteByte(VersionByte);
                writer.WriteVarUInt32((uint)CommandType.Remove);
                self._codec.WriteField(ref writer, 0, typeof(T), item!);
                writer.Commit();
            },
            (this, item));
            return true;
        }

        return false;
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected bool ApplyAdd(T item) => _items.Add(item);
    protected bool ApplyRemove(T item) => _items.Remove(item);
    protected void ApplyClear() => _items.Clear();

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private IStateMachineLogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
        return _storage;
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();
    public void ExceptWith(IEnumerable<T> other)
    {
        foreach (var item in other)
        {
            Remove(item);
        }
    }

    public bool IsProperSubsetOf(IEnumerable<T> other) => _items.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<T> other) => _items.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<T> other) => _items.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<T> other) => _items.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<T> other) => _items.Overlaps(other);
    public bool SetEquals(IEnumerable<T> other) => _items.SetEquals(other);
    void ICollection<T>.Add(T item) => Add(item);

    public void IntersectWith(IEnumerable<T> other)
    {
        var initialCount = Count;
        _items.IntersectWith(other);
        if (Count != initialCount)
        {
            GetStorage().AppendEntry(WriteSnapshotToBufferWriter, this);
        }
    }

    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        var initialCount = Count;
        _items.SymmetricExceptWith(other);
        if (Count != initialCount)
        {
            GetStorage().AppendEntry(WriteSnapshotToBufferWriter, this);
        }
    }

    public void UnionWith(IEnumerable<T> other)
    {
        foreach (var item in other)
        {
            Add(item);
        }
    }

    private enum CommandType
    {
        Add = 0,
        Remove = 1,
        Clear = 2,
        Snapshot = 3,
    }
}
