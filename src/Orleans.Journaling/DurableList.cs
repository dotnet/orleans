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
internal sealed class DurableList<T> : IDurableList<T>, IDurableStateMachine, IDurableListOperationHandler<T>
{
    private readonly IDurableListOperationCodec<T> _codec;
    private readonly List<T> _items = [];
    private ILogWriter? _storage;

    public DurableList([ServiceKey] string key, ILogManager manager, LogFormatKey logFormatKey, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = LogFormatServices.GetRequiredKeyedService<IDurableListOperationCodecProvider>(serviceProvider, logFormatKey).GetCodec<T>();
        manager.RegisterStateMachine(key, this);
    }

    internal DurableList(string key, ILogManager manager, IDurableListOperationCodec<T> codec)
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

            using var entry = GetStorage().BeginEntry();
            _codec.WriteSet(index, value, entry.Writer);
            entry.Commit();

            ApplySet(index, value);
        }
    }

    public int Count => _items.Count;

    bool ICollection<T>.IsReadOnly => false;

    void IDurableStateMachine.Reset(ILogWriter storage)
    {
        _items.Clear();
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(LogWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(LogWriter snapshotWriter)
    {
        using var entry = snapshotWriter.BeginEntry();
        _codec.WriteSnapshot(_items, entry.Writer);
        entry.Commit();
    }

    public void Add(T item)
    {
        using var entry = GetStorage().BeginEntry();
        _codec.WriteAdd(item, entry.Writer);
        entry.Commit();

        ApplyAdd(item);
    }

    public void Clear()
    {
        using var entry = GetStorage().BeginEntry();
        _codec.WriteClear(entry.Writer);
        entry.Commit();

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

        using var entry = GetStorage().BeginEntry();
        _codec.WriteInsert(index, item, entry.Writer);
        entry.Commit();

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

        using var entry = GetStorage().BeginEntry();
        _codec.WriteRemoveAt(index, entry.Writer);
        entry.Commit();

        ApplyRemoveAt(index);
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected void ApplyAdd(T item) => _items.Add(item);
    protected void ApplySet(int index, T item) => _items[index] = item;
    protected void ApplyInsert(int index, T item) => _items.Insert(index, item);
    protected void ApplyRemoveAt(int index) => _items.RemoveAt(index);
    protected void ApplyClear() => _items.Clear();
    void IDurableListOperationHandler<T>.ApplyAdd(T item) => ApplyAdd(item);
    void IDurableListOperationHandler<T>.ApplySet(int index, T item) => ApplySet(index, item);
    void IDurableListOperationHandler<T>.ApplyInsert(int index, T item) => ApplyInsert(index, item);
    void IDurableListOperationHandler<T>.ApplyRemoveAt(int index) => ApplyRemoveAt(index);
    void IDurableListOperationHandler<T>.ApplyClear() => ApplyClear();
    void IDurableListOperationHandler<T>.ApplySnapshotStart(int count)
    {
        ApplyClear();
        _items.EnsureCapacity(count);
    }

    void IDurableListOperationHandler<T>.ApplySnapshotItem(T item) => ApplyAdd(item);

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private ILogWriter GetStorage()
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
