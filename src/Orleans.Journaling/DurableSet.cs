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
internal sealed class DurableSet<T> : IDurableSet<T>, IDurableStateMachine, IDurableSetLogEntryConsumer<T>
{
    private readonly IDurableSetCodec<T> _codec;
    private readonly HashSet<T> _items = [];
    private IStateMachineLogWriter? _storage;

    public DurableSet([ServiceKey] string key, IStateMachineManager manager, IStateMachineStorage storage, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = StateMachineLogFormatServices.GetRequiredKeyedService<IDurableSetCodecProvider>(serviceProvider, storage).GetCodec<T>();
        manager.RegisterStateMachine(key, this);
    }

    internal DurableSet(string key, IStateMachineManager manager, IDurableSetCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        _codec = codec;
        manager.RegisterStateMachine(key, this);
    }

    public int Count => _items.Count;
    public bool IsReadOnly => false;

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage)
    {
        _items.Clear();
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(StateMachineLogWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineLogWriter snapshotWriter)
    {
        using var entry = snapshotWriter.BeginEntry();
        _codec.WriteSnapshot(_items, entry.Writer);
        entry.Commit();
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
    public bool Add(T item)
    {
        if (_items.Contains(item))
        {
            return false;
        }

        using var entry = GetStorage().BeginEntry();
        _codec.WriteAdd(item, entry.Writer);
        entry.Commit();

        _ = ApplyAdd(item);
        return true;
    }

    public bool Remove(T item)
    {
        if (!_items.Contains(item))
        {
            return false;
        }

        using var entry = GetStorage().BeginEntry();
        _codec.WriteRemove(item, entry.Writer);
        entry.Commit();

        _ = ApplyRemove(item);
        return true;
    }

    private void WriteSnapshot(IReadOnlyCollection<T> items)
    {
        using var entry = GetStorage().BeginEntry();
        _codec.WriteSnapshot(items, entry.Writer);
        entry.Commit();
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    protected bool ApplyAdd(T item) => _items.Add(item);
    protected bool ApplyRemove(T item) => _items.Remove(item);
    protected void ApplyClear() => _items.Clear();
    void IDurableSetLogEntryConsumer<T>.ApplyAdd(T item) => ApplyAdd(item);
    void IDurableSetLogEntryConsumer<T>.ApplyRemove(T item) => ApplyRemove(item);
    void IDurableSetLogEntryConsumer<T>.ApplyClear() => ApplyClear();
    void IDurableSetLogEntryConsumer<T>.ApplySnapshotStart(int count)
    {
        ApplyClear();
        _items.EnsureCapacity(count);
    }

    void IDurableSetLogEntryConsumer<T>.ApplySnapshotItem(T item) => ApplyAdd(item);

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private IStateMachineLogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
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
