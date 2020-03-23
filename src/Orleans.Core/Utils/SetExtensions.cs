using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans
{
    internal static class SetExtensions
    {
        /// <summary>
        /// Shortcut to create HashSet from IEnumerable that supports type inference
        /// (which the standard constructor does not)
        /// </summary>
        /// <typeparam name="T">The element type</typeparam>
        public static HashSet<T> ToSet<T>(this IEnumerable<T> values)
        {
            if (values == null)
                return null;
            return new HashSet<T>(values);
        }

        /// <summary>
        /// ToString every element of an enumeration
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        /// <param name="toString">Can supply null to use Object.ToString()</param>
        /// <param name="separator">Before each element, or space if unspecified</param>
        /// <returns></returns>
        public static string ToStrings<T>(this IEnumerable<T> list, Func<T, object> toString = null, string separator = " ")
        {
            if (list == null) return "";
            toString = toString ?? (x => x);
            //Func<T, string> toStringPrinter = (x => 
            //    {
            //        object obj = toString(x);
            //        if(obj != null)
            //            return obj.ToString();
            //        else
            //            return "null";
            //    });
            //return Utils.IEnumerableToString(list, toStringPrinter, separator);
            //Do NOT use Aggregate for string concatenation. It is very inefficient, will reallocate and copy lots of intermediate strings.
            //toString = toString ?? (x => x);
            return list.Aggregate("", (s, x) => s + separator + toString(x));
        }

        public static T GetValueOrAddNew<T, TU>(this Dictionary<TU, T> dictionary, TU key) where T : new()
        {
            T result;
            if (dictionary.TryGetValue(key, out result))
                return result;

            result = new T();
            dictionary[key] = result;
            return result;
        }
    }
}
