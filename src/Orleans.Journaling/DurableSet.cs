using System.Collections;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// Represents a set whose mutations are recorded in a journal.
/// </summary>
/// <typeparam name="T">The type of elements in the set.</typeparam>
/// <remarks>
/// The set takes ownership of the elements passed to it, and the journal records each mutation when it is made. To change an element, remove it and add a new instance.
/// </remarks>
public interface IDurableSet<T> : ISet<T>, IReadOnlyCollection<T>, IReadOnlySet<T>
{
    /// <inheritdoc/>
    new int Count { get; }

    /// <inheritdoc/>
    new bool Contains(T item);

    /// <inheritdoc/>
    new bool Add(T item);

    /// <inheritdoc/>
    new bool IsProperSubsetOf(IEnumerable<T> other);

    /// <inheritdoc/>
    new bool IsProperSupersetOf(IEnumerable<T> other);

    /// <inheritdoc/>
    new bool IsSubsetOf(IEnumerable<T> other);

    /// <inheritdoc/>
    new bool IsSupersetOf(IEnumerable<T> other);

    /// <inheritdoc/>
    new bool Overlaps(IEnumerable<T> other);

    /// <inheritdoc/>
    new bool SetEquals(IEnumerable<T> other);
}

[DebuggerTypeProxy(typeof(IDurableCollectionDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class DurableSet<T> : IDurableSet<T>, IStateMachine, IReportsStagedChanges, IDurableSetCommandHandler<T>, IResyncSource<SetChange<T>>
{
    private readonly IDurableSetCommandCodec<T> _codec;
    private readonly HashSet<T> _items = [];
    private readonly JournaledStateManager? _manager;
    private readonly JournaledStateAccessTarget _accessTarget;
    private JournalStreamWriter _writer;
    private bool _hasStagedEntries;

    public DurableSet(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IDurableSetCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        _manager = manager as JournaledStateManager;
        _accessTarget = new(key, manager);
        manager.RegisterStateMachine(key, this);
    }

    internal DurableSet(string key, IJournaledStateManager manager, IDurableSetCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _manager = manager as JournaledStateManager;
        _accessTarget = new(key, manager);
        manager.RegisterStateMachine(key, this);
    }

    public int Count => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.SetCount, this, static self => self._items.Count)
        : _items.Count;

    public bool IsReadOnly => false;

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _items.Clear();
        _writer = writer;
        _hasStagedEntries = false;
        DurableStateObservation.PublishDurableResync<DurableSet<T>, SetChange<T>>(_manager, this);
    }

    void IStateMachine.OnRecoveryCompleted() => DurableStateObservation.PublishDurableResync<DurableSet<T>, SetChange<T>>(_manager, this);

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

    public void Clear()
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.SetClear, this, static self => self.ClearCore());
            return;
        }

        ClearCore();
    }

    private void ClearCore()
    {
        _codec.WriteClear(GetWriter());
        _hasStagedEntries = true;
        ApplyClear();
        Publish(SetChangeKind.Cleared, item: default);
    }

    public bool Contains(T item) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.SetContains, (Self: this, Item: item), static state => state.Self._items.Contains(state.Item))
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

    public bool Add(T item) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.SetAdd, (Self: this, Item: item), static state => state.Self.AddCore(state.Item))
        : AddCore(item);

    private bool AddCore(T item)
    {
        if (_items.Contains(item))
        {
            return false;
        }

        _codec.WriteAdd(item, GetWriter());
        _hasStagedEntries = true;
        _ = ApplyAdd(item);
        Publish(SetChangeKind.Added, item);
        return true;
    }

    public bool Remove(T item) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.SetRemove, (Self: this, Item: item), static state => state.Self.RemoveCore(state.Item))
        : RemoveCore(item);

    private bool RemoveCore(T item)
    {
        if (!_items.Contains(item))
        {
            return false;
        }

        _codec.WriteRemove(item, GetWriter());
        _hasStagedEntries = true;
        _ = ApplyRemove(item);
        Publish(SetChangeKind.Removed, item);
        return true;
    }

    private void WriteSnapshot(IReadOnlyCollection<T> items)
    {
        _codec.WriteSnapshot(items, GetWriter());
        _hasStagedEntries = true;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // A context records the items which the code enumerates, so a replay enumerates the same items.
    private T[] ReadSnapshot(JournaledStateContext context) =>
        context.Read(_accessTarget, JournaledStateOperation.SetSnapshot, this, static self => self._items.ToArray());

    private bool ApplyAdd(T item) => _items.Add(item);
    private bool ApplyRemove(T item) => _items.Remove(item);
    private void ApplyClear() => _items.Clear();
    void IDurableSetCommandHandler<T>.ApplyAdd(T item) => ApplyAdd(item);
    void IDurableSetCommandHandler<T>.ApplyRemove(T item) => ApplyRemove(item);
    void IDurableSetCommandHandler<T>.ApplyClear() => ApplyClear();
    void IDurableSetCommandHandler<T>.Reset(int capacityHint)
    {
        ApplyClear();
        _items.EnsureCapacity(capacityHint);
    }

    private JournalStreamWriter GetWriter()
    {
        Debug.Assert(_writer.IsInitialized);
        return _writer;
    }

    public void ExceptWith(IEnumerable<T> other)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.SetExceptWith, (Self: this, Other: other), static state => state.Self.ExceptWithCore(state.Other));
            return;
        }

        ExceptWithCore(other);
    }

    private void ExceptWithCore(IEnumerable<T> other)
    {
        foreach (var item in other)
        {
            RemoveCore(item);
        }
    }

    public bool IsProperSubsetOf(IEnumerable<T> other) => Compare(JournaledStateOperation.SetIsProperSubsetOf, other, static (items, other) => items.IsProperSubsetOf(other));
    public bool IsProperSupersetOf(IEnumerable<T> other) => Compare(JournaledStateOperation.SetIsProperSupersetOf, other, static (items, other) => items.IsProperSupersetOf(other));
    public bool IsSubsetOf(IEnumerable<T> other) => Compare(JournaledStateOperation.SetIsSubsetOf, other, static (items, other) => items.IsSubsetOf(other));
    public bool IsSupersetOf(IEnumerable<T> other) => Compare(JournaledStateOperation.SetIsSupersetOf, other, static (items, other) => items.IsSupersetOf(other));
    public bool Overlaps(IEnumerable<T> other) => Compare(JournaledStateOperation.SetOverlaps, other, static (items, other) => items.Overlaps(other));
    public bool SetEquals(IEnumerable<T> other) => Compare(JournaledStateOperation.SetEquals, other, static (items, other) => items.SetEquals(other));
    void ICollection<T>.Add(T item) => Add(item);

    private bool Compare(JournaledStateOperation operation, IEnumerable<T> other, Func<HashSet<T>, IEnumerable<T>, bool> compare) =>
        JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
            ? context.Read(_accessTarget, operation, (Items: _items, Other: other, Compare: compare), static state => state.Compare(state.Items, state.Other))
            : compare(_items, other);

    public void IntersectWith(IEnumerable<T> other)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.SetIntersectWith, (Self: this, Other: other), static state => state.Self.IntersectWithCore(state.Other));
            return;
        }

        IntersectWithCore(other);
    }

    private void IntersectWithCore(IEnumerable<T> other)
    {
        var next = new HashSet<T>(_items, _items.Comparer);
        next.IntersectWith(other);
        if (!_items.SetEquals(next))
        {
            WriteSnapshot(next);
            _items.Clear();
            _items.UnionWith(next);
            DurableStateObservation.PublishStagedResync<DurableSet<T>, SetChange<T>>(_manager, this);
        }
    }

    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.SetSymmetricExceptWith, (Self: this, Other: other), static state => state.Self.SymmetricExceptWithCore(state.Other));
            return;
        }

        SymmetricExceptWithCore(other);
    }

    private void SymmetricExceptWithCore(IEnumerable<T> other)
    {
        var next = new HashSet<T>(_items, _items.Comparer);
        next.SymmetricExceptWith(other);
        if (!_items.SetEquals(next))
        {
            WriteSnapshot(next);
            _items.Clear();
            _items.UnionWith(next);
            DurableStateObservation.PublishStagedResync<DurableSet<T>, SetChange<T>>(_manager, this);
        }
    }

    public void UnionWith(IEnumerable<T> other)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.SetUnionWith, (Self: this, Other: other), static state => state.Self.UnionWithCore(state.Other));
            return;
        }

        UnionWithCore(other);
    }

    private void UnionWithCore(IEnumerable<T> other)
    {
        foreach (var item in other)
        {
            AddCore(item);
        }
    }

    internal IAsyncEnumerable<SetChange<T>> WatchAsync(DurableWatchMode mode, int capacity, CancellationToken cancellationToken) =>
        DurableStateObservation.WatchChanges<DurableSet<T>, SetChange<T>>(_manager, this, mode, capacity, cancellationToken);

    bool IResyncSource<SetChange<T>>.HasStagedEntries => _hasStagedEntries;

    SetChange<T> IResyncSource<SetChange<T>>.CreateResync() => new() { Kind = SetChangeKind.Resync, Snapshot = _items.ToArray() };

    private void Publish(SetChangeKind kind, T? item)
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.PublishStaged(new SetChange<T> { Kind = kind, Item = item });
        }
    }
}
