using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

/// <summary>
/// Represents a dictionary whose mutations are recorded in a journal.
/// </summary>
/// <typeparam name="K">The type of keys in the dictionary.</typeparam>
/// <typeparam name="V">The type of values in the dictionary.</typeparam>
/// <remarks>
/// When a value implements <see cref="IDisposable"/>, the dictionary assumes ownership after a successful mutation.
/// It disposes the value when the last reference to it is replaced or removed, when the dictionary is cleared, or
/// when recovery resets the dictionary. A value is not owned when encoding its mutation fails.
/// </remarks>
public interface IDurableDictionary<K, V> : IDictionary<K, V> where K : notnull
{
}

internal interface IDurableDictionaryOwnership<K> where K : notnull
{
    bool Remove(K key, bool disposeValue);
}

[DebuggerTypeProxy(typeof(IDurableDictionaryDebugView<,>))]
[DebuggerDisplay("Count = {Count}")]
internal class DurableDictionary<K, V> :
    IDurableDictionary<K, V>,
    IDurableDictionaryOwnership<K>,
    IJournaledState,
    IJournaledStateWriteParticipant,
    IDisposable,
    IDurableDictionaryCommandHandler<K, V>
    where K : notnull
{
    private readonly IDurableDictionaryCommandCodec<K, V> _codec;
    private readonly Dictionary<K, V> _items = [];
    private JournalStreamWriter _writer;

    protected DurableDictionary(IDurableDictionaryCommandCodec<K, V> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    public DurableDictionary(
        [ServiceKey] string key,
        IJournaledStateManager manager,
        JournaledStateManagerShared shared,
        IServiceProvider serviceProvider)
        : this(JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<K, V>>(serviceProvider, shared.JournalFormatKey))
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterState(key, this);
    }

    internal DurableDictionary(string key, IJournaledStateManager manager, IDurableDictionaryCommandCodec<K, V> codec) : this(codec)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(key);
        manager.RegisterState(key, this);
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

    void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

    void IJournaledState.Reset(JournalStreamWriter writer)
    {
        ApplyClear();
        _writer = writer;
        OnReset();
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

    public bool Contains(K key) => _items.ContainsKey(key);

    public bool Remove(K key)
        => Remove(key, disposeValue: true);

    protected bool Remove(K key, bool disposeValue)
    {
        if (!_items.ContainsKey(key))
        {
            return false;
        }

        WriteRemove(key);
        ApplyRemove(key, disposeValue);
        return true;
    }

    bool IDurableDictionaryOwnership<K>.Remove(K key, bool disposeValue) => Remove(key, disposeValue);

    private void WriteRemove(K key)
    {
        _codec.WriteRemove(key, GetWriter());
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void WriteSet(K key, V value)
    {
        _codec.WriteSet(key, value, GetWriter());
    }

    protected virtual void OnSet(K key, V value) { }

    /// <summary>
    /// Called after this state has contributed its pending mutations to the current durable write.
    /// </summary>
    protected virtual void OnWritePreparing() { }

    void IJournaledStateWriteParticipant.OnWritePreparing() => OnWritePreparing();

    /// <summary>
    /// Called when pending writes have been durably persisted.
    /// Override in derived classes to receive write completion notifications.
    /// </summary>
    protected virtual void OnWriteCompleted() { }

    /// <summary>
    /// Called when recovery resets this state before replaying durable entries.
    /// </summary>
    protected virtual void OnReset() { }

    void IJournaledState.OnWriteCompleted() => OnWriteCompleted();

    private void ApplySet(K key, V value)
    {
        if (_items.TryGetValue(key, out var previous) && !ReferenceEquals(previous, value))
        {
            _items[key] = value;
            DisposeIfUnreferenced(previous);
        }
        else
        {
            _items[key] = value;
        }

        OnSet(key, value);
    }

    internal bool ApplyRemove(K key, bool disposeValue = true)
    {
        if (!_items.Remove(key, out var value))
        {
            return false;
        }

        if (disposeValue)
        {
            DisposeIfUnreferenced(value);
        }

        return true;
    }

    private void ApplyClear()
    {
        Dictionary<object, IDisposable>? disposables = null;
        foreach (var value in _items.Values)
        {
            if (value is IDisposable disposable)
            {
                (disposables ??= new(ReferenceEqualityComparer.Instance))
                    .TryAdd(GetDisposalIdentity(value, disposable), disposable);
            }
        }

        _items.Clear();
        if (disposables is not null)
        {
            List<Exception>? exceptions = null;
            foreach (var disposable in disposables.Values)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception exception)
                {
                    (exceptions ??= []).Add(exception);
                }
            }

            if (exceptions is not null)
            {
                throw new AggregateException("One or more durable dictionary values failed to dispose.", exceptions);
            }
        }
    }

    private void DisposeIfUnreferenced(V value)
    {
        if (value is not IDisposable disposable)
        {
            return;
        }

        var identity = GetDisposalIdentity(value, disposable);
        foreach (var candidate in _items.Values)
        {
            if (candidate is IDisposable candidateDisposable
                && ReferenceEquals(GetDisposalIdentity(candidate, candidateDisposable), identity))
            {
                return;
            }
        }

        disposable.Dispose();
    }

    private static object GetDisposalIdentity(V value, IDisposable disposable) =>
        value is IJournaledResourceOwner owner ? owner.ResourceIdentity : disposable;

    void IDisposable.Dispose() => ApplyClear();
    void IDurableDictionaryCommandHandler<K, V>.ApplySet(K key, V value) => ApplySet(key, value);
    void IDurableDictionaryCommandHandler<K, V>.ApplyRemove(K key) => ApplyRemove(key);
    void IDurableDictionaryCommandHandler<K, V>.ApplyClear() => ApplyClear();
    void IDurableDictionaryCommandHandler<K, V>.Reset(int capacityHint)
    {
        ApplyClear();
        _items.EnsureCapacity(capacityHint);
        OnReset();
    }

    protected virtual JournalStreamWriter GetWriter()
    {
        Debug.Assert(_writer.IsInitialized);
        return _writer;
    }

    public IJournaledState DeepCopy() => throw new NotImplementedException();
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
        _ = ApplyRemove(item.Key);
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
