namespace Orleans.Journaling;

/// <summary>
/// Base type for all DurableDictionary log entries.
/// </summary>
public abstract record DurableDictionaryEntry<TKey, TValue>;

/// <summary>
/// Sets or updates a key-value pair in the dictionary.
/// </summary>
public sealed record DictionarySetEntry<TKey, TValue>(TKey Key, TValue Value)
    : DurableDictionaryEntry<TKey, TValue>;

/// <summary>
/// Removes a key from the dictionary.
/// </summary>
public sealed record DictionaryRemoveEntry<TKey, TValue>(TKey Key)
    : DurableDictionaryEntry<TKey, TValue>;

/// <summary>
/// Clears all items from the dictionary.
/// </summary>
public sealed record DictionaryClearEntry<TKey, TValue>()
    : DurableDictionaryEntry<TKey, TValue>;

/// <summary>
/// A snapshot of the entire dictionary state.
/// </summary>
public sealed record DictionarySnapshotEntry<TKey, TValue>(IReadOnlyList<KeyValuePair<TKey, TValue>> Items)
    : DurableDictionaryEntry<TKey, TValue>;
