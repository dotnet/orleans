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
internal sealed class DurableList<T> : IDurableList<T>, IJournaledState, IJournaledStateOperationCodecProvider, IDurableListOperationHandler<T>
{
    private readonly IDurableListOperationCodec<T> _codec;
    private readonly IServiceProvider? _serviceProvider;
    private readonly string? _journalFormatKey;
    private readonly List<T> _items = [];
    private JournalStreamWriter _storage;

    public DurableList(
        [ServiceKey] string key,
        IStateManager manager,
        [FromKeyedServices(JournalFormatServices.JournalFormatKeyServiceKey)] string journalFormatKey,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredKeyedService<IDurableListOperationCodecProvider>(serviceProvider, journalFormatKey).GetCodec<T>();
        _serviceProvider = serviceProvider;
        _journalFormatKey = journalFormatKey;
        manager.RegisterState(key, this);
    }

    internal DurableList(string key, IStateManager manager, IDurableListOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterState(key, this);
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

            _codec.WriteSet(index, value, GetStorage());
            ApplySet(index, value);
        }
    }

    public int Count => _items.Count;

    bool ICollection<T>.IsReadOnly => false;

    object IJournaledState.OperationCodec => _codec;

    object IJournaledStateOperationCodecProvider.GetOperationCodec(string journalFormatKey)
    {
        if (_journalFormatKey is null || string.Equals(journalFormatKey, _journalFormatKey, StringComparison.Ordinal))
        {
            return _codec;
        }

        return JournalFormatServices.GetRequiredKeyedService<IDurableListOperationCodecProvider>(GetServiceProvider(journalFormatKey), journalFormatKey).GetCodec<T>();
    }

    void IJournaledState.Reset(JournalStreamWriter writer)
    {
        _items.Clear();
        _storage = writer;
    }

    void IJournaledState.AppendEntries(JournalStreamWriter writer)
    {
        // This state implementation appends journal entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IJournaledState.AppendSnapshot(JournalStreamWriter snapshotWriter)
    {
        _codec.WriteSnapshot(_items, snapshotWriter);
    }

    public void Add(T item)
    {
        _codec.WriteAdd(item, GetStorage());
        ApplyAdd(item);
    }

    public void Clear()
    {
        _codec.WriteClear(GetStorage());
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

        _codec.WriteInsert(index, item, GetStorage());
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

        _codec.WriteRemoveAt(index, GetStorage());
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
    void IDurableListOperationHandler<T>.Reset(int capacityHint)
    {
        ApplyClear();
        _items.EnsureCapacity(capacityHint);
    }

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private JournalStreamWriter GetStorage()
    {
        Debug.Assert(_storage.IsInitialized);
        return _storage;
    }

    private IServiceProvider GetServiceProvider(string journalFormatKey)
        => _serviceProvider ?? throw new InvalidOperationException(
            $"State '{GetType().FullName}' cannot recover journal format key '{journalFormatKey}' because it was constructed with an explicit operation codec instead of a service provider.");

    public IJournaledState DeepCopy() => throw new NotImplementedException();
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
