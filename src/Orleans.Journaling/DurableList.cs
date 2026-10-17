using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// Represents a list whose mutations are recorded in a journal.
/// </summary>
/// <typeparam name="T">The type of elements in the list.</typeparam>
public interface IDurableList<T> : IList<T>
{
    /// <summary>
    /// Adds the elements of the specified collection to the end of the list.
    /// </summary>
    /// <param name="collection">The collection whose elements are added to the list.</param>
    void AddRange(IEnumerable<T> collection);

    /// <summary>
    /// Returns a read-only wrapper for the current list.
    /// </summary>
    /// <returns>A read-only wrapper for the current list.</returns>
    ReadOnlyCollection<T> AsReadOnly();
}

[DebuggerTypeProxy(typeof(IDurableCollectionDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class DurableList<T> : IDurableList<T>, IStateMachine, IDurableListCommandHandler<T>, IResyncSource<ListChange<T>>
{
    private readonly IDurableListCommandCodec<T> _codec;
    private readonly List<T> _items = [];
    private readonly JournaledStateManager? _manager;
    private JournalStreamWriter _writer;
    private bool _hasStagedEntries;

    public DurableList(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IDurableListCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        _manager = manager as JournaledStateManager;
        manager.RegisterStateMachine(key, this);
    }

    internal DurableList(string key, IJournaledStateManager manager, IDurableListCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _manager = manager as JournaledStateManager;
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

            _codec.WriteSet(index, value, GetWriter());
            ApplySet(index, value);
            Publish(ListChangeKind.Set, index, value);
        }
    }

    public int Count => _items.Count;

    bool ICollection<T>.IsReadOnly => false;

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _items.Clear();
        _writer = writer;
        _hasStagedEntries = false;
        PublishResync();
    }

    void IStateMachine.OnRecoveryCompleted() => PublishResync();

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer)
    {
        // This state implementation appends journal entries as the data structure is modified, so there is no need to perform separate writing here.
        // The write being captured contains every staged entry.
        _hasStagedEntries = false;
    }

    void IStateMachine.WriteSnapshot(JournalStreamWriter snapshotWriter)
    {
        _codec.WriteSnapshot(_items, snapshotWriter);
        _hasStagedEntries = false;
    }

    public void Add(T item)
    {
        _codec.WriteAdd(item, GetWriter());
        ApplyAdd(item);
        Publish(ListChangeKind.Added, _items.Count - 1, item);
    }

    public void Clear()
    {
        _codec.WriteClear(GetWriter());
        ApplyClear();
        Publish(ListChangeKind.Cleared, index: 0, item: default);
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

        _codec.WriteInsert(index, item, GetWriter());
        ApplyInsert(index, item);
        Publish(ListChangeKind.Inserted, index, item);
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

        _codec.WriteRemoveAt(index, GetWriter());
        ApplyRemoveAt(index);
        Publish(ListChangeKind.RemovedAt, index, item: default);
    }

    internal IAsyncEnumerable<ListChange<T>> WatchAsync(DurableWatchMode mode, int capacity, CancellationToken cancellationToken) =>
        DurableStateObservation.WatchChanges<DurableList<T>, ListChange<T>>(_manager, this, mode, capacity, cancellationToken);

    bool IResyncSource<ListChange<T>>.HasStagedEntries => _hasStagedEntries;

    ListChange<T> IResyncSource<ListChange<T>>.CreateResync() => new() { Kind = ListChangeKind.Resync, Snapshot = _items.ToArray() };

    private void Publish(ListChangeKind kind, int index, T? item)
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.Publish(new ListChange<T> { Kind = kind, Index = index, Item = item });
        }
    }

    private void PublishResync()
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.Publish(((IResyncSource<ListChange<T>>)this).CreateResync(), isDurable: true);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void ApplyAdd(T item) => _items.Add(item);
    private void ApplySet(int index, T item) => _items[index] = item;
    private void ApplyInsert(int index, T item) => _items.Insert(index, item);
    private void ApplyRemoveAt(int index) => _items.RemoveAt(index);
    private void ApplyClear() => _items.Clear();
    void IDurableListCommandHandler<T>.ApplyAdd(T item) => ApplyAdd(item);
    void IDurableListCommandHandler<T>.ApplySet(int index, T item) => ApplySet(index, item);
    void IDurableListCommandHandler<T>.ApplyInsert(int index, T item) => ApplyInsert(index, item);
    void IDurableListCommandHandler<T>.ApplyRemoveAt(int index) => ApplyRemoveAt(index);
    void IDurableListCommandHandler<T>.ApplyClear() => ApplyClear();
    void IDurableListCommandHandler<T>.Reset(int capacityHint)
    {
        ApplyClear();
        _items.EnsureCapacity(capacityHint);
    }

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private JournalStreamWriter GetWriter()
    {
        Debug.Assert(_writer.IsInitialized);
        JournaledStateMutationFence.ThrowIfClosed();
        _hasStagedEntries = true;
        return _writer;
    }

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
