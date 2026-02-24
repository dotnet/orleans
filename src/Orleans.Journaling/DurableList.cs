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
internal sealed class DurableList<T> : IDurableList<T>, IDurableStateMachine
{
    private readonly ILogEntryCodec<DurableListEntry<T>> _entryCodec;
    private readonly List<T> _items = [];
    private IStateMachineLogWriter? _storage;

    public DurableList([ServiceKey] string key, IStateMachineManager manager, IDurableListCodecProvider codecProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _entryCodec = codecProvider.GetCodec<T>();
        manager.RegisterStateMachine(key, this);
    }

    internal DurableList(string key, IStateMachineManager manager, ILogEntryCodec<DurableListEntry<T>> entryCodec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _entryCodec = entryCodec;
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
                self._entryCodec.Write(new ListSetEntry<T>(index, value!), bufferWriter);
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
        var entry = _entryCodec.Read(logEntry);
        switch (entry)
        {
            case ListAddEntry<T>(var item):
                ApplyAdd(item);
                break;
            case ListSetEntry<T>(var index, var value):
                ApplySet(index, value);
                break;
            case ListInsertEntry<T>(var index, var value):
                ApplyInsert(index, value);
                break;
            case ListRemoveAtEntry<T>(var index):
                ApplyRemoveAt(index);
                break;
            case ListClearEntry<T>:
                ApplyClear();
                break;
            case ListSnapshotEntry<T>(var items):
                ApplyClear();
                _items.EnsureCapacity(items.Count);
                foreach (var item in items)
                {
                    ApplyAdd(item);
                }

                break;
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
            self._entryCodec.Write(
                new ListSnapshotEntry<T>(self._items.ToList()), bufferWriter);
        }, this);
    }

    public void Add(T item)
    {
        ApplyAdd(item);
        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            var (self, item) = state;
            self._entryCodec.Write(new ListAddEntry<T>(item!), bufferWriter);
        },
        (this, item));
    }

    public void Clear()
    {
        ApplyClear();
        GetStorage().AppendEntry(static (self, bufferWriter) =>
        {
            self._entryCodec.Write(new ListClearEntry<T>(), bufferWriter);
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
            self._entryCodec.Write(new ListInsertEntry<T>(index, value!), bufferWriter);
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
            self._entryCodec.Write(new ListRemoveAtEntry<T>(index), bufferWriter);
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