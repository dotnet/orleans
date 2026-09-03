using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// Represents a first-in, first-out collection whose mutations are recorded in a journal.
/// </summary>
/// <typeparam name="T">The type of elements in the queue.</typeparam>
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
internal sealed class DurableQueue<T> : IDurableQueue<T>, IJournaledState, IDurableQueueCommandHandler<T>
{
    private readonly IDurableQueueCommandCodec<T> _codec;
    private readonly Queue<T> _items = new();
    private JournalStreamWriter _writer;

    public DurableQueue(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredCommandCodec<IDurableQueueCommandCodec<T>>(serviceProvider, shared.JournalFormatKey);
        manager.RegisterState(key, this);
    }

    internal DurableQueue(string key, IJournaledStateManager manager, IDurableQueueCommandCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterState(key, this);
    }

    public int Count => _items.Count;

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IJournaledState.Reset(JournalStreamWriter writer)
    {
        _items.Clear();
        _writer = writer;
    }

    void IJournaledState.AppendEntries(JournalStreamWriter writer)
    {
        // This state implementation appends journal entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IJournaledState.AppendSnapshot(JournalStreamWriter snapshotWriter)
    {
        _codec.WriteSnapshot(_items, snapshotWriter);
    }

    public void Clear()
    {
        _codec.WriteClear(GetWriter());
        ApplyClear();
    }

    public T Peek() => _items.Peek();
    public bool TryPeek([MaybeNullWhen(false)] out T item) => _items.TryPeek(out item);
    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    public void Enqueue(T item)
    {
        _codec.WriteEnqueue(item, GetWriter());
        ApplyEnqueue(item);
    }

    public T Dequeue()
    {
        var result = _items.Peek();
        _codec.WriteDequeue(GetWriter());
        _ = ApplyDequeue();
        return result;
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item)
    {
        if (!_items.TryPeek(out item))
        {
            return false;
        }

        _codec.WriteDequeue(GetWriter());
        _ = ApplyTryDequeue(out _);
        return true;
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void ApplyEnqueue(T item) => _items.Enqueue(item);
    private T ApplyDequeue() => _items.Dequeue();
    private bool ApplyTryDequeue([MaybeNullWhen(false)] out T value) => _items.TryDequeue(out value);
    private void ApplyClear() => _items.Clear();
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

    public IJournaledState DeepCopy() => throw new NotImplementedException();
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
