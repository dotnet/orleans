using System.Collections.Generic;

namespace Orleans.Serialization.Activators
{
    public class DictionaryActivator<TKey, TValue>
    {
        public Dictionary<TKey, TValue> Create(IEqualityComparer<TKey> arg) => new Dictionary<TKey, TValue>(arg);
    }
}