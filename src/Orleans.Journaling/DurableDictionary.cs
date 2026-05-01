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
internal class DurableDictionary<K, V> : IDurableDictionary<K, V>, IDurableStateMachine, IDurableDictionaryOperationHandler<K, V> where K : notnull
{
    private readonly IDurableDictionaryOperationCodec<K, V> _codec;
    private readonly Dictionary<K, V> _items = [];
    private LogWriter _storage;

    protected DurableDictionary(IDurableDictionaryOperationCodec<K, V> codec)
    {
        _codec = codec;
    }

    public DurableDictionary(
        [ServiceKey] string key,
        ILogManager manager,
        [FromKeyedServices(LogFormatServices.LogFormatKeyServiceKey)] string logFormatKey,
        IServiceProvider serviceProvider)
        : this(LogFormatServices.GetRequiredKeyedService<IDurableDictionaryOperationCodecProvider>(serviceProvider, logFormatKey).GetCodec<K, V>())
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterStateMachine(key, this);
    }

    internal DurableDictionary(string key, ILogManager manager, IDurableDictionaryOperationCodec<K, V> codec) : this(codec)
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

    object IDurableStateMachine.OperationCodec => _codec;

    void IDurableStateMachine.Reset(LogWriter storage)
    {
        _items.Clear();
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        _codec.Apply(logEntry, this);
    }

    void IDurableStateMachine.AppendEntries(LogWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(LogWriter snapshotWriter)
    {
        _codec.WriteSnapshot(_items, snapshotWriter);
    }

    public void Clear()
    {
        _codec.WriteClear(GetStorage());
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
        _codec.WriteRemove(key, GetStorage());
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void WriteSet(K key, V value)
    {
        _codec.WriteSet(key, value, GetStorage());
    }

    protected virtual void OnSet(K key, V value) { }

    private void ApplySet(K key, V value)
    {
        _items[key] = value;
        OnSet(key, value);
    }

    internal bool ApplyRemove(K key) => _items.Remove(key);
    private void ApplyClear() => _items.Clear();
    void IDurableDictionaryOperationHandler<K, V>.ApplySet(K key, V value) => ApplySet(key, value);
    void IDurableDictionaryOperationHandler<K, V>.ApplyRemove(K key) => ApplyRemove(key);
    void IDurableDictionaryOperationHandler<K, V>.ApplyClear() => ApplyClear();
    void IDurableDictionaryOperationHandler<K, V>.ApplySnapshotStart(int count)
    {
        ApplyClear();
        _items.EnsureCapacity(count);
    }

    void IDurableDictionaryOperationHandler<K, V>.ApplySnapshotItem(K key, V value) => ApplySet(key, value);

    protected virtual LogWriter GetStorage()
    {
        Debug.Assert(_storage.IsInitialized);
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
