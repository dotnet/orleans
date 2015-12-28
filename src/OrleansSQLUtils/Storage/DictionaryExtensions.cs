using System;
using System.Collections.Generic;

namespace Orleans.SqlUtils
{
    /// <summary>
    /// Extensions methods to work with dictionaries.
    /// </summary>
    public static class DictionaryExtensions
    {
        /// <summary>
        /// Gets the value or a default for the given type if not found.
        /// </summary>
        /// <typeparam name="TKey">The type of a key with which to search for.</typeparam>
        /// <typeparam name="TValue">The type of a value which to return.</typeparam>
        /// <param name="dictionary">The dictionary from which to search from.</param>
        /// <param name="key">The key with which to search.</param>
        /// <returns>The found object or <em>default(TValue)</em>.</returns>
        public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
        {
            TValue value;
            dictionary.TryGetValue(key, out value);
            return value;
        }


        /// <summary>
        /// Gets the value or a default for the given type if not found.
        /// </summary>
        /// <typeparam name="TKey">The type of a key with which to search for.</typeparam>
        /// <typeparam name="TValue">The type of a value which to return.</typeparam>
        /// <param name="dictionary">The dictionary from which to search from.</param>
        /// <param name="key">The key with which to search.</param>
        /// <param name="defaultProvider">A provider for a default value if the value was not found.</param>
        /// <returns>The found object or value as defined by <see paramref="defaultProvider"/>.</returns>
        public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, Func<TValue> defaultProvider)
        {
            TValue value;
            return dictionary.TryGetValue(key, out value) ? value : defaultProvider();
        }
    }
}
