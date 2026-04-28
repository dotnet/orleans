using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

public interface IDurableDictionary<K, V> : IDictionary<K, V> where K : notnull
{
}

[DebuggerTypeProxy(typeof(IDurableDictionaryDebugView<,>))]
[DebuggerDisplay("Count = {Count}")]
internal class DurableDictionary<K, V> : IDurableDictionary<K, V>, IDurableStateMachine, IDurableDictionaryLogEntryConsumer<K, V> where K : notnull
{
    private readonly IDurableDictionaryCodec<K, V> _codec;
    private readonly Dictionary<K, V> _items = [];
    private IStateMachineLogWriter? _storage;

    protected DurableDictionary(IDurableDictionaryCodec<K, V> codec)
    {
        _codec = codec;
    }

    public DurableDictionary([ServiceKey] string key, IStateMachineManager manager, IDurableDictionaryCodecProvider codecProvider) : this(codecProvider.GetCodec<K, V>())
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterStateMachine(key, this);
    }

    internal DurableDictionary(string key, IStateMachineManager manager, IDurableDictionaryCodec<K, V> codec) : this(codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterStateMachine(key, this);
    }

    public V this[K key]
    {
        get => _items[key];

        set
        {
            WriteSet(key, value);
            ApplySet(key, value);
        }
    }

    public int Count => _items.Count;

    public ICollection<K> Keys => _items.Keys;

    public ICollection<V> Values => _items.Values;

    public bool IsReadOnly => ((ICollection<KeyValuePair<K, V>>)_items).IsReadOnly;

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage)
    {
        _items.Clear();
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter)
    {
        WriteSnapshot(snapshotWriter.BeginEntry());
    }

    public void Clear()
    {
        var writer = GetStorage().BeginEntry();
        try
        {
            _codec.WriteClear(writer);
            writer.Commit();
        }
        catch
        {
            writer.Abort();
            throw;
        }

        ApplyClear();
    }

    public bool Contains(K key) => _items.ContainsKey(key);

    public bool Remove(K key)
    {
        if (!_items.ContainsKey(key))
        {
            return false;
        }

        WriteRemove(key);
        ApplyRemove(key);
        return true;
    }

    private void WriteRemove(K key)
    {
        var writer = GetStorage().BeginEntry();
        try
        {
            _codec.WriteRemove(key, writer);
            writer.Commit();
        }
        catch
        {
            writer.Abort();
            throw;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void WriteSet(K key, V value)
    {
        var writer = GetStorage().BeginEntry();
        try
        {
            _codec.WriteSet(key, value, writer);
            writer.Commit();
        }
        catch
        {
            writer.Abort();
            throw;
        }
    }

    private void WriteSnapshot(LogEntryWriter writer)
    {
        try
        {
            _codec.WriteSnapshot(_items, _items.Count, writer);
            writer.Commit();
        }
        catch
        {
            writer.Abort();
            throw;
        }
    }

    protected virtual void OnSet(K key, V value) { }

    private void ApplySet(K key, V value)
    {
        _items[key] = value;
        OnSet(key, value);
    }

    internal bool ApplyRemove(K key) => _items.Remove(key);
    private void ApplyClear() => _items.Clear();
    void IDurableDictionaryLogEntryConsumer<K, V>.ApplySet(K key, V value) => ApplySet(key, value);
    void IDurableDictionaryLogEntryConsumer<K, V>.ApplyRemove(K key) => ApplyRemove(key);
    void IDurableDictionaryLogEntryConsumer<K, V>.ApplyClear() => ApplyClear();
    void IDurableDictionaryLogEntryConsumer<K, V>.ApplySnapshotStart(int count)
    {
        ApplyClear();
        _items.EnsureCapacity(count);
    }

    void IDurableDictionaryLogEntryConsumer<K, V>.ApplySnapshotItem(K key, V value) => ApplySet(key, value);

    protected virtual IStateMachineLogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
        return _storage;
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();
    public void Add(K key, V value)
    {
        if (_items.ContainsKey(key))
        {
            ThrowDuplicateKey(key);
        }

        WriteSet(key, value);
        _items.Add(key, value);
        OnSet(key, value);
    }

    public bool ContainsKey(K key) => _items.ContainsKey(key);
    public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value) => _items.TryGetValue(key, out value);
    public void Add(KeyValuePair<K, V> item) => Add(item.Key, item.Value);
    public bool Contains(KeyValuePair<K, V> item) => _items.Contains(item);
    public void CopyTo(KeyValuePair<K, V>[] array, int arrayIndex) => ((ICollection<KeyValuePair<K, V>>)_items).CopyTo(array, arrayIndex);
    public bool Remove(KeyValuePair<K, V> item)
    {
        if (!((ICollection<KeyValuePair<K, V>>)_items).Contains(item))
        {
            return false;
        }

        WriteRemove(item.Key);
        _ = ((ICollection<KeyValuePair<K, V>>)_items).Remove(item);
        return true;
    }

    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => ((IEnumerable<KeyValuePair<K, V>>)_items).GetEnumerator();

    [DoesNotReturn]
    private static void ThrowDuplicateKey(K key) => throw new ArgumentException($"An item with the same key has already been added. Key: {key}", nameof(key));
}

[DebuggerDisplay("{Value}", Name = "[{Key}]")]
internal readonly struct DebugViewDictionaryItem<TKey, TValue>
{
    public DebugViewDictionaryItem(TKey key, TValue value)
    {
        Key = key;
        Value = value;
    }

    public DebugViewDictionaryItem(KeyValuePair<TKey, TValue> keyValue)
    {
        Key = keyValue.Key;
        Value = keyValue.Value;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
    public TKey Key { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
    public TValue Value { get; }
}

internal sealed class IDurableDictionaryDebugView<TKey, TValue> where TKey : notnull
{
    private readonly IDurableDictionary<TKey, TValue> _dict;

    public IDurableDictionaryDebugView(IDurableDictionary<TKey, TValue> dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        _dict = dictionary;
    }

    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public DebugViewDictionaryItem<TKey, TValue>[] Items
    {
        get
        {
            var keyValuePairs = new KeyValuePair<TKey, TValue>[_dict.Count];
            _dict.CopyTo(keyValuePairs, 0);
            var items = new DebugViewDictionaryItem<TKey, TValue>[keyValuePairs.Length];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = new DebugViewDictionaryItem<TKey, TValue>(keyValuePairs[i]);
            }
            return items;
        }
    }
}
