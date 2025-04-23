using System.Buffers;
using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

public interface IDurableList<T> : IList<T>
{
    void AddRange(IEnumerable<T> collection);
    ReadOnlyCollection<T> AsReadOnly();
}

[DebuggerTypeProxy(typeof(IDurableCollectionDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class DurableList<T> : IDurableList<T>, IDurableStateMachine
{
    private readonly SerializerSessionPool _serializerSessionPool;
    private readonly IFieldCodec<T> _codec;
    private const byte VersionByte = 0;
    private readonly List<T> _items = [];
    private IStateMachineLogWriter? _storage;

    public DurableList([ServiceKey] string key, IStateMachineManager manager, IFieldCodec<T> codec, SerializerSessionPool serializerSessionPool)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _serializerSessionPool = serializerSessionPool;
        manager.RegisterStateMachine(key, this);
    }

    public T this[int index]
    {
        get => _items[index];

        set
        {
            if ((uint)index >= (uint)_items.Count)
            {
                ThrowIndexOutOfRange();
            }

            ApplySet(index, value);
            GetStorage().AppendEntry(static (state, bufferWriter) =>
            {
                var (self, index, value) = state;
                using var session = self._serializerSessionPool.GetSession();
                var writer = Writer.Create(bufferWriter, session);
                writer.WriteByte(VersionByte);
                writer.WriteVarUInt32((uint)CommandType.Set);
                writer.WriteVarUInt32((uint)index);
                self._codec.WriteField(ref writer, 0, typeof(T), value!);
                writer.Commit();
            },
            (this, index, value));
        }
    }

    public int Count => _items.Count;

    bool ICollection<T>.IsReadOnly => false;

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
            throw new NotSupportedException($"This instance of {nameof(DurableList<T>)} supports version {(uint)VersionByte} and not version {(uint)version}.");
        }

        var commandType = (CommandType)reader.ReadVarUInt32();
        switch (commandType)
        {
            case CommandType.Add:
                ApplyAdd(ReadValue(ref reader));
                break;
            case CommandType.Set:
                {
                    var index = (int)reader.ReadVarUInt32();
                    var value = ReadValue(ref reader);
                    ApplySet(index, value);
                }
                break;
            case CommandType.Insert:
                {
                    var index = (int)reader.ReadVarUInt32();
                    var value = ReadValue(ref reader);
                    ApplyInsert(index, value);
                }
                break;
            case CommandType.Remove:
                ApplyRemoveAt((int)reader.ReadVarUInt32());
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
            var count = reader.ReadVarUInt32();
            ApplyClear();
            _items.EnsureCapacity((int)count);
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
        snapshotWriter.AppendEntry(static (self, bufferWriter) =>
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
        }, this);
    }

    public void Add(T item)
    {
        ApplyAdd(item);
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
    public int IndexOf(T item) => _items.IndexOf(item);
    public void Insert(int index, T item)
    {
        ApplyInsert(index, item);
        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            var (self, index, value) = state;
            using var session = self._serializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.Insert);
            writer.WriteVarUInt32((uint)index);
            self._codec.WriteField(ref writer, 0, typeof(T), value!);
            writer.Commit();
        },
        (this, index, item));
    }

    public bool Remove(T item)
    {
        var index = _items.IndexOf(item);
        if (index >= 0)
        {
            RemoveAt(index);
            return true;
        }

        return false;
    }

    public void RemoveAt(int index)
    {
        ApplyRemoveAt(index);

        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            var (self, index) = state;
            using var session = self._serializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.Remove);
            writer.WriteVarUInt32((uint)index);
            writer.Commit();
        }, (this, index));
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected void ApplyAdd(T item) => _items.Add(item);
    protected void ApplySet(int index, T item) => _items[index] = item;
    protected void ApplyInsert(int index, T item) => _items.Insert(index, item);
    protected void ApplyRemoveAt(int index) => _items.RemoveAt(index);
    protected void ApplyClear() => _items.Clear();

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private IStateMachineLogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
        return _storage;
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();
    public void AddRange(IEnumerable<T> collection)
    {
        foreach (var element in collection)
        {
            Add(element);
        }
    }

    public ReadOnlyCollection<T> AsReadOnly() => _items.AsReadOnly();

    private enum CommandType
    {
        Add = 0,
        Set = 1,
        Insert = 2,
        Remove = 3,
        Clear = 4,
        Snapshot = 5
    }
}

internal sealed class IDurableCollectionDebugView<T>
{
    private readonly ICollection<T> _collection;

    public IDurableCollectionDebugView(ICollection<T> collection)
    {
#if NET
        ArgumentNullException.ThrowIfNull(collection);
#else
            if (collection is null)
            {
                throw new ArgumentNullException(nameof(collection));
            }
#endif

        _collection = collection;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public T[] Items
    {
        get
        {
            T[] items = new T[_collection.Count];
            _collection.CopyTo(items, 0);
            return items;
        }
    }
}