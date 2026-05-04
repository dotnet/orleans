using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public interface IDurableSet<T> : ISet<T>, IReadOnlyCollection<T>, IReadOnlySet<T>
{
    new int Count { get; }
    new bool Contains(T item);
    new bool Add(T item);
    new bool IsProperSubsetOf(IEnumerable<T> other);
    new bool IsProperSupersetOf(IEnumerable<T> other);
    new bool IsSubsetOf(IEnumerable<T> other);
    new bool IsSupersetOf(IEnumerable<T> other);
    new bool Overlaps(IEnumerable<T> other);
    new bool SetEquals(IEnumerable<T> other);
}

[DebuggerTypeProxy(typeof(IDurableCollectionDebugView<>))]
[DebuggerDisplay("Count = {Count}")]
internal sealed class DurableSet<T> : IDurableSet<T>, IDurableStateMachine, IDurableSetOperationHandler<T>
{
    private readonly IDurableSetOperationCodec<T> _codec;
    private readonly HashSet<T> _items = [];
    private LogStreamWriter _storage;

    public DurableSet(
        [ServiceKey] string key,
        IStateMachineManager manager,
        [FromKeyedServices(LogFormatServices.LogFormatKeyServiceKey)] string logFormatKey,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = LogFormatServices.GetRequiredKeyedService<IDurableSetOperationCodecProvider>(serviceProvider, logFormatKey).GetCodec<T>();
        manager.RegisterStateMachine(key, this);
    }

    internal DurableSet(string key, IStateMachineManager manager, IDurableSetOperationCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterStateMachine(key, this);
    }

    public int Count => _items.Count;
    public bool IsReadOnly => false;

    object IDurableStateMachine.OperationCodec => _codec;

    void IDurableStateMachine.Reset(LogStreamWriter writer)
    {
        _items.Clear();
        _storage = writer;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(LogStreamWriter writer)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(LogStreamWriter snapshotWriter)
    {
        _codec.WriteSnapshot(_items, snapshotWriter);
    }

    public void Clear()
    {
        _codec.WriteClear(GetStorage());
        ApplyClear();
    }

    public bool Contains(T item) => _items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    public bool Add(T item)
    {
        if (_items.Contains(item))
        {
            return false;
        }

        _codec.WriteAdd(item, GetStorage());
        _ = ApplyAdd(item);
        return true;
    }

    public bool Remove(T item)
    {
        if (!_items.Contains(item))
        {
            return false;
        }

        _codec.WriteRemove(item, GetStorage());
        _ = ApplyRemove(item);
        return true;
    }

    private void WriteSnapshot(IReadOnlyCollection<T> items)
    {
        _codec.WriteSnapshot(items, GetStorage());
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected bool ApplyAdd(T item) => _items.Add(item);
    protected bool ApplyRemove(T item) => _items.Remove(item);
    protected void ApplyClear() => _items.Clear();
    void IDurableSetOperationHandler<T>.ApplyAdd(T item) => ApplyAdd(item);
    void IDurableSetOperationHandler<T>.ApplyRemove(T item) => ApplyRemove(item);
    void IDurableSetOperationHandler<T>.ApplyClear() => ApplyClear();
    void IDurableSetOperationHandler<T>.Reset(int capacityHint)
    {
        ApplyClear();
        _items.EnsureCapacity(capacityHint);
    }

    private LogStreamWriter GetStorage()
    {
        Debug.Assert(_storage.IsInitialized);
        return _storage;
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();
    public void ExceptWith(IEnumerable<T> other)
    {
        foreach (var item in other)
        {
            Remove(item);
        }
    }

    public bool IsProperSubsetOf(IEnumerable<T> other) => _items.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<T> other) => _items.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<T> other) => _items.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<T> other) => _items.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<T> other) => _items.Overlaps(other);
    public bool SetEquals(IEnumerable<T> other) => _items.SetEquals(other);
    void ICollection<T>.Add(T item) => Add(item);

    public void IntersectWith(IEnumerable<T> other)
    {
        var next = new HashSet<T>(_items, _items.Comparer);
        next.IntersectWith(other);
        if (!_items.SetEquals(next))
        {
            WriteSnapshot(next);
            _items.Clear();
            _items.UnionWith(next);
        }
    }

    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        var next = new HashSet<T>(_items, _items.Comparer);
        next.SymmetricExceptWith(other);
        if (!_items.SetEquals(next))
        {
            WriteSnapshot(next);
            _items.Clear();
            _items.UnionWith(next);
        }
    }

    public void UnionWith(IEnumerable<T> other)
    {
        foreach (var item in other)
        {
            Add(item);
        }
    }
}
