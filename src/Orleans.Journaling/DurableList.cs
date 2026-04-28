using System.Buffers;
using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public interface IDurableList<T> : IList<T>
{
    void AddRange(IEnumerable<T> collection);
    ReadOnlyCollection<T> AsReadOnly();
}

[DebuggerTypeProxy(typeof(IDurableCollectionDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class DurableList<T> : IDurableList<T>, IDurableStateMachine, IDurableListLogEntryConsumer<T>
{
    private readonly IDurableListCodec<T> _codec;
    private readonly List<T> _items = [];
    private IStateMachineLogWriter? _storage;

    public DurableList([ServiceKey] string key, IStateMachineManager manager, IDurableListCodecProvider codecProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codecProvider.GetCodec<T>();
        manager.RegisterStateMachine(key, this);
    }

    internal DurableList(string key, IStateMachineManager manager, IDurableListCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
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

            var writer = GetStorage().BeginEntry();
            try
            {
                _codec.WriteSet(index, value, writer);
                writer.Commit();
            }
            catch
            {
                writer.Abort();
                throw;
            }

            ApplySet(index, value);
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
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter)
    {
        var writer = snapshotWriter.BeginEntry();
        try
        {
            _codec.WriteSnapshot(_items, _items.Count, writer);
            writer.Commit();
        }
        catch
        {
            writer.Abort();
            throw;
        }
    }

    public void Add(T item)
    {
        var writer = GetStorage().BeginEntry();
        try
        {
            _codec.WriteAdd(item, writer);
            writer.Commit();
        }
        catch
        {
            writer.Abort();
            throw;
        }

        ApplyAdd(item);
    }

    public void Clear()
    {
        var writer = GetStorage().BeginEntry();
        try
        {
            _codec.WriteClear(writer);
            writer.Commit();
        }
        catch
        {
            writer.Abort();
            throw;
        }

        ApplyClear();
    }

    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    public int IndexOf(T item) => _items.IndexOf(item);
    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_items.Count)
        {
            ThrowIndexOutOfRange();
        }

        var writer = GetStorage().BeginEntry();
        try
        {
            _codec.WriteInsert(index, item, writer);
            writer.Commit();
        }
        catch
        {
            writer.Abort();
            throw;
        }

        ApplyInsert(index, item);
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
        if ((uint)index >= (uint)_items.Count)
        {
            ThrowIndexOutOfRange();
        }

        var writer = GetStorage().BeginEntry();
        try
        {
            _codec.WriteRemoveAt(index, writer);
            writer.Commit();
        }
        catch
        {
            writer.Abort();
            throw;
        }

        ApplyRemoveAt(index);
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected void ApplyAdd(T item) => _items.Add(item);
    protected void ApplySet(int index, T item) => _items[index] = item;
    protected void ApplyInsert(int index, T item) => _items.Insert(index, item);
    protected void ApplyRemoveAt(int index) => _items.RemoveAt(index);
    protected void ApplyClear() => _items.Clear();
    void IDurableListLogEntryConsumer<T>.ApplyAdd(T item) => ApplyAdd(item);
    void IDurableListLogEntryConsumer<T>.ApplySet(int index, T item) => ApplySet(index, item);
    void IDurableListLogEntryConsumer<T>.ApplyInsert(int index, T item) => ApplyInsert(index, item);
    void IDurableListLogEntryConsumer<T>.ApplyRemoveAt(int index) => ApplyRemoveAt(index);
    void IDurableListLogEntryConsumer<T>.ApplyClear() => ApplyClear();
    void IDurableListLogEntryConsumer<T>.ApplySnapshotStart(int count)
    {
        ApplyClear();
        _items.EnsureCapacity(count);
    }

    void IDurableListLogEntryConsumer<T>.ApplySnapshotItem(T item) => ApplyAdd(item);

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
}

internal sealed class IDurableCollectionDebugView<T>
{
    private readonly ICollection<T> _collection;

    public IDurableCollectionDebugView(ICollection<T> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
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
