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
/// <remarks>
/// The list takes ownership of the elements passed to it, and the journal records each mutation when it is made. To change an element, assign a new instance.
/// </remarks>
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
internal sealed class DurableList<T> : IDurableList<T>, IStateMachine, IReportsStagedChanges, IDurableListCommandHandler<T>, IResyncSource<ListChange<T>>
{
    private readonly IDurableListCommandCodec<T> _codec;
    private readonly List<T> _items = [];
    private readonly JournaledStateManager? _manager;
    private readonly JournaledStateAccessTarget _accessTarget;
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
        _accessTarget = new(key, manager);
        manager.RegisterStateMachine(key, this);
    }

    internal DurableList(string key, IJournaledStateManager manager, IDurableListCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _manager = manager as JournaledStateManager;
        _accessTarget = new(key, manager);
        manager.RegisterStateMachine(key, this);
    }

    public T this[int index]
    {
        get => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
            ? context.Read(_accessTarget, JournaledStateOperation.ListGet, index, (Self: this, Index: index), static state => state.Self._items[state.Index])
            : _items[index];

        set
        {
            if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
            {
                context.Write(_accessTarget, JournaledStateOperation.ListSet, (index, value), (Self: this, Index: index, Value: value), static state => state.Self.SetCore(state.Index, state.Value));
                return;
            }

            SetCore(index, value);
        }
    }

    private void SetCore(int index, T value)
    {
        if ((uint)index >= (uint)_items.Count)
        {
            ThrowIndexOutOfRange();
        }

        _codec.WriteSet(index, value, GetWriter());
        _hasStagedEntries = true;
        ApplySet(index, value);
        Publish(ListChangeKind.Set, index, value);
    }

    public int Count => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.ListCount, this, static self => self._items.Count)
        : _items.Count;

    bool ICollection<T>.IsReadOnly => false;

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        ApplyClear();
        _writer = writer;
        _hasStagedEntries = false;
        DurableStateObservation.PublishDurableResync<DurableList<T>, ListChange<T>>(_manager, this);
    }

    void IStateMachine.OnRecoveryCompleted() => DurableStateObservation.PublishDurableResync<DurableList<T>, ListChange<T>>(_manager, this);

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
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.ListAdd, item, (Self: this, Item: item), static state => state.Self.AddCore(state.Item));
            return;
        }

        AddCore(item);
    }

    private void AddCore(T item)
    {
        _codec.WriteAdd(item, GetWriter());
        _hasStagedEntries = true;
        ApplyAdd(item);
        Publish(ListChangeKind.Added, _items.Count - 1, item);
    }

    public void Clear()
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.ListClear, this, static self => self.ClearCore());
            return;
        }

        ClearCore();
    }

    private void ClearCore()
    {
        _codec.WriteClear(GetWriter());
        _hasStagedEntries = true;
        ApplyClear();
        Publish(ListChangeKind.Cleared, index: 0, item: default);
    }

    public bool Contains(T item) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.ListContains, item, (Self: this, Item: item), static state => state.Self._items.Contains(state.Item))
        : _items.Contains(item);

    public void CopyTo(T[] array, int arrayIndex)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            ReadSnapshot(context).CopyTo(array, arrayIndex);
            return;
        }

        _items.CopyTo(array, arrayIndex);
    }

    public IEnumerator<T> GetEnumerator() => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? ((IEnumerable<T>)ReadSnapshot(context)).GetEnumerator()
        : _items.GetEnumerator();

    public int IndexOf(T item) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.ListIndexOf, item, (Self: this, Item: item), static state => state.Self._items.IndexOf(state.Item))
        : _items.IndexOf(item);

    public void Insert(int index, T item)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.ListInsert, (index, item), (Self: this, Index: index, Item: item), static state => state.Self.InsertCore(state.Index, state.Item));
            return;
        }

        InsertCore(index, item);
    }

    private void InsertCore(int index, T item)
    {
        if ((uint)index > (uint)_items.Count)
        {
            ThrowIndexOutOfRange();
        }

        _codec.WriteInsert(index, item, GetWriter());
        _hasStagedEntries = true;
        ApplyInsert(index, item);
        Publish(ListChangeKind.Inserted, index, item);
    }

    public bool Remove(T item) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.ListRemove, item, (Self: this, Item: item), static state => state.Self.RemoveCore(state.Item))
        : RemoveCore(item);

    private bool RemoveCore(T item)
    {
        var index = _items.IndexOf(item);
        if (index >= 0)
        {
            RemoveAtCore(index);
            return true;
        }

        return false;
    }

    public void RemoveAt(int index)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.ListRemoveAt, index, (Self: this, Index: index), static state => state.Self.RemoveAtCore(state.Index));
            return;
        }

        RemoveAtCore(index);
    }

    private void RemoveAtCore(int index)
    {
        if ((uint)index >= (uint)_items.Count)
        {
            ThrowIndexOutOfRange();
        }

        _codec.WriteRemoveAt(index, GetWriter());
        _hasStagedEntries = true;
        ApplyRemoveAt(index);
        Publish(ListChangeKind.RemovedAt, index, item: default);
    }

    // A context records the items which the code enumerates, so a replay enumerates the same items.
    private T[] ReadSnapshot(JournaledStateContext context) =>
        context.Read(_accessTarget, JournaledStateOperation.ListSnapshot, this, static self => self._items.ToArray());

    internal IAsyncEnumerable<ListChange<T>> WatchAsync(DurableWatchMode mode, int capacity, CancellationToken cancellationToken) =>
        DurableStateObservation.WatchChanges<DurableList<T>, ListChange<T>>(_manager, this, mode, capacity, cancellationToken);

    bool IResyncSource<ListChange<T>>.HasStagedEntries => _hasStagedEntries;

    ListChange<T> IResyncSource<ListChange<T>>.CreateResync() => new() { Kind = ListChangeKind.Resync, Snapshot = _items.ToArray() };

    private void Publish(ListChangeKind kind, int index, T? item)
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.PublishStaged(new ListChange<T> { Kind = kind, Index = index, Item = item });
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void ApplyAdd(T item)
    {
        _accessTarget.OnChanged();
        _items.Add(item);
    }

    private void ApplySet(int index, T item)
    {
        _accessTarget.OnChanged();
        _items[index] = item;
    }

    private void ApplyInsert(int index, T item)
    {
        _accessTarget.OnChanged();
        _items.Insert(index, item);
    }

    private void ApplyRemoveAt(int index)
    {
        _accessTarget.OnChanged();
        _items.RemoveAt(index);
    }

    private void ApplyClear()
    {
        _accessTarget.OnChanged();
        _items.Clear();
    }
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
        return _writer;
    }

    public void AddRange(IEnumerable<T> collection)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.ListAddRange, (Self: this, Items: collection), static state => state.Self.AddRangeCore(state.Items));
            return;
        }

        AddRangeCore(collection);
    }

    private void AddRangeCore(IEnumerable<T> collection)
    {
        foreach (var element in collection)
        {
            AddCore(element);
        }
    }

    public ReadOnlyCollection<T> AsReadOnly() => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? new ReadOnlyCollection<T>(ReadSnapshot(context))
        : _items.AsReadOnly();
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
