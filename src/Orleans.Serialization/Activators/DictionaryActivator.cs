using System.Collections.Generic;

namespace Orleans.Serialization.Activators
{
    /// <summary>
    /// Creates dictionary objects.
    /// </summary>
    /// <typeparam name="TKey">The dictionary key type.</typeparam>
    /// <typeparam name="TValue">The dictionary value type.</typeparam>
    public class DictionaryActivator<TKey, TValue>
    {
        /// <summary>
        /// Creates a new dictionary.
        /// </summary>
        /// <param name="arg">The equality comparer.</param>
        /// <returns>A new dictionary.</returns>
        public Dictionary<TKey, TValue> Create(IEqualityComparer<TKey> arg) => new Dictionary<TKey, TValue>(arg);
    }
}