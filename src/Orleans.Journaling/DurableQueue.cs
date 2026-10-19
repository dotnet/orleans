using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Orleans.Journaling;

/// <summary>
/// Represents a first-in, first-out collection whose mutations are recorded in a journal.
/// </summary>
/// <typeparam name="T">The type of elements in the queue.</typeparam>
/// <remarks>
/// The queue takes ownership of the elements passed to it, and the journal records each mutation when it is made. To change an element, enqueue a new instance.
/// </remarks>
public interface IDurableQueue<T> : IEnumerable<T>, IReadOnlyCollection<T>
{
    /// <summary>
    /// Removes all elements from the queue.
    /// </summary>
    void Clear();

    /// <summary>
    /// Determines whether the queue contains the specified element.
    /// </summary>
    /// <param name="item">The element to locate in the queue.</param>
    /// <returns><see langword="true"/> if the queue contains <paramref name="item"/>; otherwise, <see langword="false"/>.</returns>
    bool Contains(T item);

    /// <summary>
    /// Copies the queue elements to an array, starting at the specified array index.
    /// </summary>
    /// <param name="array">The destination array.</param>
    /// <param name="arrayIndex">The zero-based index in <paramref name="array"/> at which copying begins.</param>
    void CopyTo(T[] array, int arrayIndex);

    /// <summary>
    /// Removes and returns the element at the beginning of the queue.
    /// </summary>
    /// <returns>The element removed from the beginning of the queue.</returns>
    T Dequeue();

    /// <summary>
    /// Adds an element to the end of the queue.
    /// </summary>
    /// <param name="item">The element to add to the queue.</param>
    void Enqueue(T item);

    /// <summary>
    /// Returns the element at the beginning of the queue without removing it.
    /// </summary>
    /// <returns>The element at the beginning of the queue.</returns>
    T Peek();

    /// <summary>
    /// Attempts to remove and return the element at the beginning of the queue.
    /// </summary>
    /// <param name="item">The removed element, or the default value of <typeparamref name="T"/> when the queue is empty.</param>
    /// <returns><see langword="true"/> if an element was removed; otherwise, <see langword="false"/>.</returns>
    bool TryDequeue([MaybeNullWhen(false)] out T item);

    /// <summary>
    /// Attempts to return the element at the beginning of the queue without removing it.
    /// </summary>
    /// <param name="item">The element at the beginning of the queue, or the default value of <typeparamref name="T"/> when the queue is empty.</param>
    /// <returns><see langword="true"/> if an element was returned; otherwise, <see langword="false"/>.</returns>
    bool TryPeek([MaybeNullWhen(false)] out T item);
}

[DebuggerTypeProxy(typeof(DurableQueueDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class DurableQueue<T> : IDurableQueue<T>, IStateMachine, IReportsStagedChanges, IDurableQueueCommandHandler<T>, IResyncSource<QueueChange<T>>, IJournaledStateHistory, IJournaledStateReplica
{
    private readonly IDurableQueueCommandCodec<T> _codec;
    private readonly Queue<T> _items = new();
    private readonly JournaledStateManager? _manager;
    private readonly JournaledStateAccessTarget _accessTarget;
    private JournalStreamWriter _writer;
    private bool _hasStagedEntries;

    public DurableQueue(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IDurableQueueCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        _manager = manager as JournaledStateManager;
        _accessTarget = new(key, manager, this);
        manager.RegisterStateMachine(key, this);
    }

    internal DurableQueue(string key, IJournaledStateManager manager, IDurableQueueCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        _manager = manager as JournaledStateManager;
        _accessTarget = new(key, manager, this);
        manager.RegisterStateMachine(key, this);
    }

    // A replica, which has no manager, so its reads run directly.
    private DurableQueue(IDurableQueueCommandCodec<T> codec)
    {
        _codec = codec;
        _accessTarget = new(nameof(DurableQueue<T>));
    }

    // The collection journals each change as it makes it.
    void IJournaledStateHistory.StagePendingChanges() { }

    IJournaledStateReplica IJournaledStateHistory.CreateReplica() => new DurableQueue<T>(_codec);

    void IJournaledStateReplica.Evaluate(JournaledStateOperation operation, ReadOnlyMemory<byte> argument, Serializer serializer, IBufferWriter<byte> result)
    {
        switch (operation)
        {
            case JournaledStateOperation.QueueCount:
                JournaledStateReplicaEncoding.WriteResult(serializer, _items.Count, result);
                break;
            case JournaledStateOperation.QueuePeek:
                JournaledStateReplicaEncoding.WriteResult(serializer, _items.Peek(), result);
                break;
            case JournaledStateOperation.QueueTryPeek:
                JournaledStateReplicaEncoding.WriteResult(serializer, _items.TryPeek(out var item) ? (true, item) : (false, default(T)!), result);
                break;
            case JournaledStateOperation.QueueContains:
                JournaledStateReplicaEncoding.WriteResult(serializer, _items.Contains(JournaledStateReplicaEncoding.ReadArgument<T>(serializer, argument)), result);
                break;
            case JournaledStateOperation.QueueSnapshot:
                JournaledStateReplicaEncoding.WriteResult(serializer, _items.ToArray(), result);
                break;
            default:
                throw JournaledStateReplicaEncoding.NotPositional(operation, nameof(DurableQueue<T>));
        }
    }
    public int Count => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.ReadWhole(_accessTarget, JournaledStateOperation.QueueCount, this, static self => self._items.Count)
        : _items.Count;

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        ApplyClear();
        _writer = writer;
        _hasStagedEntries = false;
        DurableStateObservation.PublishDurableResync<DurableQueue<T>, QueueChange<T>>(_manager, this);
    }

    void IStateMachine.OnRecoveryCompleted() => DurableStateObservation.PublishDurableResync<DurableQueue<T>, QueueChange<T>>(_manager, this);

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
            context.Write(_accessTarget, JournaledStateOperation.QueueClear, this, static self => self.ClearCore());
            return;
        }

        ClearCore();
    }

    private void ClearCore()
    {
        _codec.WriteClear(GetWriter());
        _hasStagedEntries = true;
        ApplyClear();
        Publish(QueueChangeKind.Cleared, item: default);
    }

    public T Peek() => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.ReadWhole(_accessTarget, JournaledStateOperation.QueuePeek, this, static self => self._items.Peek())
        : _items.Peek();

    public bool TryPeek([MaybeNullWhen(false)] out T item)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            (var found, item) = context.ReadWhole(
                _accessTarget,
                JournaledStateOperation.QueueTryPeek,
                this,
                static self => self._items.TryPeek(out var item) ? (true, item) : (false, default(T)!));
            return found;
        }

        return _items.TryPeek(out item);
    }

    public bool Contains(T item) => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Read(_accessTarget, JournaledStateOperation.QueueContains, item, (Self: this, Item: item), static state => state.Self._items.Contains(state.Item))
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

    public void Enqueue(T item)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            context.Write(_accessTarget, JournaledStateOperation.QueueEnqueue, item, (Self: this, Item: item), static state => state.Self.EnqueueCore(state.Item));
            return;
        }

        EnqueueCore(item);
    }

    private void EnqueueCore(T item)
    {
        _codec.WriteEnqueue(item, GetWriter());
        _hasStagedEntries = true;
        ApplyEnqueue(item);
        Publish(QueueChangeKind.Enqueued, item);
    }

    // A context records the item before the dequeue removes it, so an item which it can't record stays in the queue.
    public T Dequeue() => JournaledStateContext.TryGetCurrent(_accessTarget, out var context)
        ? context.Write(_accessTarget, JournaledStateOperation.QueueDequeue, this, static self => self._items.Peek(), static (self, _) => self.DequeueCore())
        : DequeueCore();

    private T DequeueCore()
    {
        var result = _items.Peek();
        _codec.WriteDequeue(GetWriter());
        _hasStagedEntries = true;
        _ = ApplyDequeue();
        Publish(QueueChangeKind.Dequeued, result);
        return result;
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item)
    {
        if (JournaledStateContext.TryGetCurrent(_accessTarget, out var context))
        {
            (var found, item) = context.Write(
                _accessTarget,
                JournaledStateOperation.QueueTryDequeue,
                this,
                static self => self._items.TryPeek(out var item) ? (true, item) : (false, default(T)!),
                static (self, result) =>
                {
                    if (result.Item1)
                    {
                        _ = self.TryDequeueCore(out _);
                    }
                });
            return found;
        }

        return TryDequeueCore(out item);
    }

    private bool TryDequeueCore([MaybeNullWhen(false)] out T item)
    {
        if (!_items.TryPeek(out item))
        {
            return false;
        }

        _codec.WriteDequeue(GetWriter());
        _hasStagedEntries = true;
        _ = ApplyTryDequeue(out _);
        Publish(QueueChangeKind.Dequeued, item);
        return true;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // A context records the items which the code enumerates, so a replay enumerates the same items.
    private T[] ReadSnapshot(JournaledStateContext context) =>
        context.ReadWhole(_accessTarget, JournaledStateOperation.QueueSnapshot, this, static self => self._items.ToArray());

    internal IAsyncEnumerable<QueueChange<T>> WatchAsync(DurableWatchMode mode, int capacity, CancellationToken cancellationToken) =>
        DurableStateObservation.WatchChanges<DurableQueue<T>, QueueChange<T>>(_manager, this, mode, capacity, cancellationToken);

    bool IResyncSource<QueueChange<T>>.HasStagedEntries => _hasStagedEntries;

    QueueChange<T> IResyncSource<QueueChange<T>>.CreateResync() => new() { Kind = QueueChangeKind.Resync, Snapshot = _items.ToArray() };

    private void Publish(QueueChangeKind kind, T? item)
    {
        if (DurableStateObservation.TryGetTopic(_manager, this, out var topic))
        {
            topic.PublishStaged(new QueueChange<T> { Kind = kind, Item = item });
        }
    }

    private void ApplyEnqueue(T item)
    {
        _accessTarget.OnChanged();
        _items.Enqueue(item);
    }

    private T ApplyDequeue()
    {
        _accessTarget.OnChanged();
        return _items.Dequeue();
    }

    private bool ApplyTryDequeue([MaybeNullWhen(false)] out T value)
    {
        _accessTarget.OnChanged();
        return _items.TryDequeue(out value);
    }

    private void ApplyClear()
    {
        _accessTarget.OnChanged();
        _items.Clear();
    }
    void IDurableQueueCommandHandler<T>.ApplyEnqueue(T item) => ApplyEnqueue(item);
    void IDurableQueueCommandHandler<T>.ApplyDequeue() => _ = ApplyDequeue();
    void IDurableQueueCommandHandler<T>.ApplyClear() => ApplyClear();
    void IDurableQueueCommandHandler<T>.Reset(int capacityHint)
    {
        ApplyClear();
        _items.EnsureCapacity(capacityHint);
    }

    private JournalStreamWriter GetWriter()
    {
        Debug.Assert(_writer.IsInitialized);
        return _writer;
    }

}

internal sealed class DurableQueueDebugView<T>
{
    private readonly DurableQueue<T> _queue;

    public DurableQueueDebugView(DurableQueue<T> queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        _queue = queue;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public T[] Items
    {
        get
        {
            return _queue.ToArray();
        }
    }
}
