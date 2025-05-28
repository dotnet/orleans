using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Caching.Internal;

/// <summary>
/// Represents a generic cache of key/value pairs.
/// </summary>
/// <typeparam name="K">The type of keys in the cache.</typeparam>
/// <typeparam name="V">The type of values in the cache.</typeparam>
// Source: https://github.com/bitfaster/BitFaster.Caching/blob/5b2d64a1afcc251787fbe231c6967a62820fc93c/BitFaster.Caching/ICache.cs
internal interface ICache<K, V> : IEnumerable<KeyValuePair<K, V>>
{
    /// <summary>
    /// Gets the number of items currently held in the cache.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the cache metrics, if configured.
    /// </summary>
    ICacheMetrics Metrics { get; }

    /// <summary>
    /// Gets a collection containing the keys in the cache.
    /// </summary>
    ICollection<K> Keys { get; }

    /// <summary>
    /// Attempts to add the specified key and value to the cache if the key does not already exist.
    /// </summary>
    /// <param name="key">The key of the element to add.</param>
    /// <param name="value">The value of the element to add.</param>
    /// <returns>true if the key/value pair was added to the cache; otherwise, false.</returns>
    bool TryAdd(K key, V value);

    /// <summary>
    /// Attempts to get the value associated with the specified key from the cache.
    /// </summary>
    /// <param name="key">The key of the value to get.</param>
    /// <param name="value">When this method returns, contains the object from the cache that has the specified key, or the default value of the type if the operation failed.</param>
    /// <returns>true if the key was found in the cache; otherwise, false.</returns>
    bool TryGet(K key, [MaybeNullWhen(false)] out V value);

    /// <summary>
    /// Gets the value associated with the specified key from the cache.
    /// </summary>
    /// <param name="key">The key of the value to get.</param>
    /// <returns>The value.</returns>
    V Get(K key);

    /// <summary>
    /// Adds a key/value pair to the cache if the key does not already exist. Returns the new value, or the 
    /// existing value if the key already exists.
    /// </summary>
    /// <param name="key">The key of the element to add.</param>
    /// <param name="valueFactory">The factory function used to generate a value for the key.</param>
    /// <returns>The value for the key. This will be either the existing value for the key if the key is already 
    /// in the cache, or the new value if the key was not in the cache.</returns>
    V GetOrAdd(K key, Func<K, V> valueFactory);

    /// <summary>
    /// Adds a key/value pair to the cache if the key does not already exist. Returns the new value, or the 
    /// existing value if the key already exists.
    /// </summary>
    /// <typeparam name="TArg">The type of an argument to pass into valueFactory.</typeparam>
    /// <param name="key">The key of the element to add.</param>
    /// <param name="valueFactory">The factory function used to generate a value for the key.</param>
    /// <param name="factoryArgument">An argument value to pass into valueFactory.</param>
    /// <returns>The value for the key. This will be either the existing value for the key if the key is already 
    /// in the cache, or the new value if the key was not in the cache.</returns>
    /// <remarks>The default implementation given here is the fallback that provides backwards compatibility for classes that implement ICache on prior versions</remarks>
    V GetOrAdd<TArg>(K key, Func<K, TArg, V> valueFactory, TArg factoryArgument) => GetOrAdd(key, k => valueFactory(k, factoryArgument));

    /// <summary>
    /// Attempts to remove and return the value that has the specified key.
    /// </summary>
    /// <param name="key">The key of the element to remove.</param>
    /// <param name="value">When this method returns, contains the object removed, or the default value of the value type if key does not exist.</param>
    /// <returns>true if the object was removed successfully; otherwise, false.</returns>
    bool TryRemove(K key, [MaybeNullWhen(false)] out V value);

    /// <summary>
    /// Attempts to remove the specified key value pair.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    /// <returns>true if the item was removed successfully; otherwise, false.</returns>
    bool TryRemove(KeyValuePair<K, V> item);

    /// <summary>
    /// Attempts to remove the value that has the specified key.
    /// </summary>
    /// <param name="key">The key of the element to remove.</param>
    /// <returns>true if the object was removed successfully; otherwise, false.</returns>
    bool TryRemove(K key);

    /// <summary>
    /// Attempts to update the value that has the specified key.
    /// </summary>
    /// <param name="key">The key of the element to update.</param>
    /// <param name="value">The new value.</param>
    /// <returns>true if the object was updated successfully; otherwise, false.</returns>
    bool TryUpdate(K key, V value);

    /// <summary>
    /// Adds a key/value pair to the cache if the key does not already exist, or updates a key/value pair if the 
    /// key already exists.
    /// </summary>
    /// <param name="key">The key of the element to update.</param>
    /// <param name="value">The new value.</param>
    void AddOrUpdate(K key, V value);

    /// <summary>
    /// Removes all keys and values from the cache.
    /// </summary>
    void Clear();
}
