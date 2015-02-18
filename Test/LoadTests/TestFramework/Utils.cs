﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
﻿using System.Globalization;
﻿using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading;


namespace Orleans
{
    /// <summary>
    /// The Utils class contains a variety of utility methods for use in application and grain code.
    /// </summary>
    internal static class Utils
    {
        ///// <summary>
        ///// Gets an application logger.
        ///// </summary>
        ///// <param name="loggerName">The name to use for this logger, typically the name of the application component that will write log entries.</param>
        ///// <returns>A Logger object</returns>
        //public static Logger GetApplicationLogger(string loggerName)
        //{
        //    return Logger.GetLogger(loggerName, Logger.LoggerType.Application);
        //}
        ///// <summary>
        ///// Gets an application logger for a specified application class.
        ///// </summary>
        ///// <param name="caller">The application code class that will use this logger.
        ///// The class's short name is used as the name of the log.</param>
        ///// <returns>A Logger object</returns>
        //public static Logger GetApplicationLogger(Type caller)
        //{
        //    return Logger.GetLogger(caller.Name, Logger.LoggerType.Application);
        //}

        /// <summary>
        /// Returns a human-readable text string that describes an array of objects.
        /// </summary>
        /// <typeparam name="T">The type of the array elements.</typeparam>
        /// <param name="array">The array to describe.</param>
        /// <returns>A string assembled by wrapping the string descriptions of the individual
        /// elements with square brackets and separating them with commas.</returns>
        public static string ArrayToString<T>(T[] array)
        {
            if (array == null || array.Length == 0)
            {
                return "[]";
            }
            StringBuilder str = new StringBuilder("[");
            bool firstDone = false;
            for (int i = 0; i < array.Length; i++)
            {
                string val = array[i] == null ? "null" : array[i].ToString();
                if (firstDone)
                {
                    str.Append(", ");
                    str.Append(val);
                }
                else
                {
                    str.Append(val);
                    firstDone = true;
                }
            }
            str.Append("]");
            return str.ToString();
        }

        /// <summary>
        /// Returns a human-readable text string that describes an IEnumerable collection of objects.
        /// </summary>
        /// <typeparam name="T">The type of the list elements.</typeparam>
        /// <param name="collection">The IEnumerable to describe.</param>
        /// <param name="toString"></param>
        /// <param name="separator"></param>
        /// <param name="putInBrackets"></param>
        /// <returns>A string assembled by wrapping the string descriptions of the individual
        /// elements with square brackets and separating them with commas.</returns>
        public static string IEnumerableToString<T>(IEnumerable<T> collection, Func<T, string> toString = null, 
                                                        string separator = ", ", bool putInBrackets = true)
        {
            if (collection == null)
            {
                if (putInBrackets) return "[]";
                else return "null";
            }
            StringBuilder str = new StringBuilder();
            if (putInBrackets) str.Append("[");
            IEnumerator<T> enumerator = collection.GetEnumerator();
            bool firstDone = false;
            while (enumerator.MoveNext())
            {
                T value = enumerator.Current;
                string val = null;
                if (toString != null)
                    val = toString(value);
                else
                    val = value==null ? "null" : value.ToString();

                if (firstDone)
                {
                    str.Append(separator);
                    str.Append(val);
                }
                else
                {
                    str.Append(val);
                    firstDone = true;
                }
            }
            if (putInBrackets) str.Append("]");
            return str.ToString();
        }

        /// <summary>
        /// Returns a human-readable text string that describes a dictionary that maps objects to objects.
        /// </summary>
        /// <typeparam name="T1">The type of the dictionary keys.</typeparam>
        /// <typeparam name="T2">The type of the dictionary elements.</typeparam>
        /// <param name="dict">The dictionary to describe.</param>
        /// <param name="separator"></param>
        /// <returns>A string assembled by wrapping the string descriptions of the individual
        /// pairs with square brackets and separating them with commas.
        /// Each key-value pair is represented as the string description of the key followed by
        /// the string description of the value,
        /// separated by " -> ", and enclosed in curly brackets.</returns>
        public static string DictionaryToString<T1, T2>(ICollection<KeyValuePair<T1, T2>> dict, string separator = "\n")
        {
            if (dict == null || dict.Count == 0)
            {
                return "[]";
            }
            StringBuilder str = new StringBuilder("[");
            IEnumerator<KeyValuePair<T1, T2>> enumerator = dict.GetEnumerator();
            int index = 0;
            while (enumerator.MoveNext())
            {
                KeyValuePair<T1, T2> pair = enumerator.Current;
                str.Append("{");
                str.Append(pair.Key);
                str.Append(" -> ");
                str.Append(pair.Value);
                str.Append("}");
                //str += "{" + pair.Key + " -> " + pair.Value + "}";
                if (index++ < dict.Count - 1)
                    str.Append(separator);
            }
            str.Append("]");
            return str.ToString();
        }

        /// <summary>
        /// Returns a human-readable text string that describes a dictionary that maps objects to lists of objects.
        /// </summary>
        /// <typeparam name="T1">The type of the dictionary keys.</typeparam>
        /// <typeparam name="T2">The type of the list elements.</typeparam>
        /// <param name="dict">The dictionary to describe.</param>
        /// <returns>A string assembled by wrapping the string descriptions of the individual
        /// pairs with square brackets and separating them with commas.
        /// Each key-value pair is represented as the string descripotion of the key followed by
        /// the string description of the value list (created using <see cref="IEnumerableToString"/>),
        /// separated by " -> ", and enclosed in curly brackets.</returns>
        public static string DictionaryOfListsToString<T1, T2>(Dictionary<T1, List<T2>> dict)
        {
            if (dict == null || dict.Count == 0)
            {
                return "[]";
            }
            StringBuilder str = new StringBuilder("[");
            Dictionary<T1, List<T2>>.Enumerator enumerator = dict.GetEnumerator();
            while (enumerator.MoveNext())
            {
                KeyValuePair<T1, List<T2>> pair = enumerator.Current;
                str.Append("{");
                str.Append(pair.Key);
                str.Append(" -> ");
                str.Append(Utils.IEnumerableToString(pair.Value));
                str.Append("}\n");
                //str += "{" + pair.Key + " -> " + Utils.IEnumerableToString(pair.Value) + "}" + "\n";
            }
            str.Append("]");
            return str.ToString();
        }

        /// <summary>
        /// Calculates an integer hash value based on the SHA1 hash of a string.
        /// </summary>
        /// <param name="text">The string to hash.</param>
        /// <returns>An integer hash for the string.</returns>
        public static int CalculateSHA1(string text)
        {
            SHA1 sha = new SHA1CryptoServiceProvider(); // This is one implementation of the abstract class SHA1.
            int hash = 0;
            try
            {
                byte[] data = Encoding.Unicode.GetBytes(text);
                byte[] result = sha.ComputeHash(data);
                //Debug.Assert((result.Length % 4) == 0); // SHA1 is 160 bits
                for (int i = 0; i < result.Length; i += 4)
                {
                    int tmp = (result[i] << 24) | (result[i + 1] << 16) | (result[i + 2] << 8) | (result[i + 3]);
                    hash = hash ^ tmp;
                }
                //string hash = BitConverter.ToString(cryptoTransformSHA1.ComputeHash(buffer)).Replace("-", "");
            }
            finally
            {
                sha.Dispose();
            }
            return hash;
        }

        

        /// <summary>
        /// This method is for internal use only.
        /// </summary>
        /// <param name="types"></param>
        /// <returns></returns>
        public static IEnumerable<Type> LeafTypes(this IEnumerable<Type> types)
        {
            return types.Where(i => !types.Any(i2 => i != i2 && i.IsAssignableFrom(i2)));
        }

        /// <summary>
        /// This method is for internal use only.
        /// Shortcut to create KeyValuePair that supports type inference
        /// (which the standard constructor does not)
        /// </summary>
        /// <typeparam name="TK"></typeparam>
        /// <typeparam name="TV"></typeparam>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static KeyValuePair<TK, TV> KeyValue<TK, TV>(this TK key, TV value)
        {
            return new KeyValuePair<TK, TV>(key, value);
        }

        /// <summary>
        /// Partitions a stream based on a predicate
        /// </summary>
        /// <typeparam name="T">Values</typeparam>
        /// <param name="values">Values</param>
        /// <param name="test">Predicate</param>
        /// <returns>Array[0] = values for which Predicate is false, [1] = ... true</returns>
        public static HashSet<T>[] Partition<T>(this IEnumerable<T> values, Func<T,bool> test)
        {
            var no = new HashSet<T>();
            var yes = new HashSet<T>();
            foreach (var value in values)
            {
                (test(value) ? yes : no).Add(value);
            }
            return new[] {no, yes};
        }

        /// <summary>
        /// This method is for internal use only.
        /// </summary>
        /// <typeparam name="TK"></typeparam>
        /// <typeparam name="TV"></typeparam>
        /// <param name="map"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool HasValue<TK, TV>(this Dictionary<TK, TV> map, TK key, TV value)
        {
            TV old;
            return map.TryGetValue(key, out old) && old.Equals(value);
        }

        ///// <summary>
        ///// Fold a list of values together by applying a function between each element
        ///// e.g. ...fold(fold(values[0], values[1]), values[2]) ...)
        ///// </summary>
        ///// <typeparam name="T">Element type</typeparam>
        ///// <param name="values">Non-empty list of values</param>
        ///// <param name="fold">Function to combine two values</param>
        ///// <returns>Result of applying function</returns>
        //public static T Fold<T>(this IEnumerable<T> values, Func<T,T,T> fold)
        //{
        //    return values.Skip(1).Aggregate(values.First(), fold);
        //}

        public static byte[] ParseHexBytes(this string s)
        {
            var result = new byte[s.Length / 2];
            for (int i = 0; i < s.Length - 1; i += 2) // allow for \r at end of line
            {
                result[i / 2] = (byte)Int32.Parse(s.Substring(i, 2), NumberStyles.HexNumber);
            }
            return result;
        }

        public static string ToHexString(this byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
