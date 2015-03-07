using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans
{
    static class DictionaryExtensions
    {
        public static TResult Find<TKey, TResult>(this IDictionary<TKey, TResult> dictionary, TKey key) where TResult : class
        {
            TResult result;
            return !dictionary.TryGetValue(key, out result) ? null : result;
        }

        public static TResult Find<TKey, TResult>(this IDictionary<TKey, TResult> dictionary, TKey key, TResult @default) where TResult : struct
        {
            TResult result;
            return !dictionary.TryGetValue(key, out result) ? @default : result;
        }
    }
}
