using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public interface IDurableQueue<T> : IEnumerable<T>, IReadOnlyCollection<T>
{
    void Clear();
    bool Contains(T item);
    void CopyTo(T[] array, int arrayIndex);
    T Dequeue();
    void Enqueue(T item);
    T Peek();
    bool TryDequeue([MaybeNullWhen(false)] out T item);
    bool TryPeek([MaybeNullWhen(false)] out T item);
}

[DebuggerTypeProxy(typeof(DurableQueueDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class DurableQueue<T> : IDurableQueue<T>, IJournaledState, IJournaledStateOperationCodecProvider, IQueueOperationHandler<T>
{
    private readonly IQueueOperationCodec<T> _codec;
    private readonly Queue<T> _items = new();
    private JournalStreamWriter _storage;

    public DurableQueue(
        [ServiceKey] string key,
        IStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = JournalFormatServices.GetRequiredOperationCodec<IQueueOperationCodec<T>>(serviceProvider, shared.JournalFormatKey);
        manager.RegisterState(key, this);
    }

    internal DurableQueue(string key, IStateManager manager, IQueueOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterState(key, this);
    }

    public int Count => _items.Count;

    object IJournaledStateOperationCodecProvider.OperationCodec => _codec;

    Type IJournaledState.OperationCodecServiceType => typeof(IQueueOperationCodec<T>);

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

    public void Clear()
    {
        _codec.WriteClear(GetStorage());
        ApplyClear();
    }

    public T Peek() => _items.Peek();
    public bool TryPeek([MaybeNullWhen(false)] out T item) => _items.TryPeek(out item);
    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    public void Enqueue(T item)
    {
        _codec.WriteEnqueue(item, GetStorage());
        ApplyEnqueue(item);
    }

    public T Dequeue()
    {
        var result = _items.Peek();
        _codec.WriteDequeue(GetStorage());
        _ = ApplyDequeue();
        return result;
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item)
    {
        if (!_items.TryPeek(out item))
        {
            return false;
        }

        _codec.WriteDequeue(GetStorage());
        _ = ApplyTryDequeue(out _);
        return true;
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected void ApplyEnqueue(T item) => _items.Enqueue(item);
    protected T ApplyDequeue() => _items.Dequeue();
    protected bool ApplyTryDequeue([MaybeNullWhen(false)] out T value) => _items.TryDequeue(out value);
    protected void ApplyClear() => _items.Clear();
    void IQueueOperationHandler<T>.ApplyEnqueue(T item) => ApplyEnqueue(item);
    void IQueueOperationHandler<T>.ApplyDequeue() => _ = ApplyDequeue();
    void IQueueOperationHandler<T>.ApplyClear() => ApplyClear();
    void IQueueOperationHandler<T>.Reset(int capacityHint)
    {
        ApplyClear();
        _items.EnsureCapacity(capacityHint);
    }

    private JournalStreamWriter GetStorage()
    {
        Debug.Assert(_storage.IsInitialized);
        return _storage;
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
