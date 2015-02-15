/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿﻿using System;
﻿using System.Collections;
﻿using System.Collections.Generic;
﻿using System.Linq;
﻿using System.Reflection;
﻿using System.Security.Cryptography;
﻿using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// The Utils class contains a variety of utility methods for use in application and grain code.
    /// </summary>
    internal static class Utils
    {
        /// <summary>
        /// Returns a human-readable text string that describes an IEnumerable collection of objects.
        /// </summary>
        /// <typeparam name="T">The type of the list elements.</typeparam>
        /// <param name="collection">The IEnumerable to describe.</param>
        /// <returns>A string assembled by wrapping the string descriptions of the individual
        /// elements with square brackets and separating them with commas.</returns>
        public static string EnumerableToString<T>(IEnumerable<T> collection, Func<T, string> toString = null, 
                                                        string separator = ", ", bool putInBrackets = true)
        {
            if (collection == null)
            {
                if (putInBrackets) return "[]";
                else return "null";
            }
            var sb = new StringBuilder();
            if (putInBrackets) sb.Append("[");
            var enumerator = collection.GetEnumerator();
            bool firstDone = false;
            while (enumerator.MoveNext())
            {
                T value = enumerator.Current;
                string val;
                if (toString != null)
                    val = toString(value);
                else
                    val = value == null ? "null" : value.ToString();

                if (firstDone)
                {
                    sb.Append(separator);
                    sb.Append(val);
                }
                else
                {
                    sb.Append(val);
                    firstDone = true;
                }
            }
            if (putInBrackets) sb.Append("]");
            return sb.ToString();
        }

        /// <summary>
        /// Returns a human-readable text string that describes a dictionary that maps objects to objects.
        /// </summary>
        /// <typeparam name="T1">The type of the dictionary keys.</typeparam>
        /// <typeparam name="T2">The type of the dictionary elements.</typeparam>
        /// <param name="separateWithNewLine">Whether the elements should appear separated by a new line.</param>
        /// <param name="dict">The dictionary to describe.</param>
        /// <returns>A string assembled by wrapping the string descriptions of the individual
        /// pairs with square brackets and separating them with commas.
        /// Each key-value pair is represented as the string description of the key followed by
        /// the string description of the value,
        /// separated by " -> ", and enclosed in curly brackets.</returns>
        public static string DictionaryToString<T1, T2>(ICollection<KeyValuePair<T1, T2>> dict, Func<T2, string> toString = null, string separator = null)
        {
            if (dict == null || dict.Count == 0)
            {
                return "[]";
            }
            if (separator == null)
            {
                separator = Environment.NewLine;
            }
            var sb = new StringBuilder("[");
            var enumerator = dict.GetEnumerator();
            int index = 0;
            while (enumerator.MoveNext())
            {
                var pair = enumerator.Current;
                sb.Append("{");
                sb.Append(pair.Key);
                sb.Append(" -> ");

                string val;
                if (toString != null)
                    val = toString(pair.Value);
                else
                    val = pair.Value == null ? "null" : pair.Value.ToString();
                sb.Append(val);

                sb.Append("}");
                if (index++ < dict.Count - 1)
                    sb.Append(separator);
            }
            sb.Append("]");
            return sb.ToString();
        }

        public static string TimeSpanToString(TimeSpan timeSpan)
        {
            //00:03:32.8289777
            return String.Format("{0}h:{1}m:{2}s.{3}ms", timeSpan.Hours, timeSpan.Minutes, timeSpan.Seconds, timeSpan.Milliseconds);
        }

        public static long TicksToMilliSeconds(long ticks)
        {
            return (long)TimeSpan.FromTicks(ticks).TotalMilliseconds;
        }

        public static float AverageTicksToMilliSeconds(float ticks)
        {
            return (float)TimeSpan.FromTicks((long)ticks).TotalMilliseconds;
        }

        /// <summary>
        /// Parse a Uri as an IPEndpoint.
        /// </summary>
        /// <param name="uri">The input Uri</param>
        /// <returns></returns>
        public static System.Net.IPEndPoint ToIPEndPoint(this Uri uri)
        {
            switch (uri.Scheme)
            {
                case "gwy.tcp":
                    return new System.Net.IPEndPoint(System.Net.IPAddress.Parse(uri.Host), uri.Port);
            }
            return null;
        }

        /// <summary>
        /// Parse a Uri as a Silo address, including the IPEndpoint and generation identifier.
        /// </summary>
        /// <param name="uri">The input Uri</param>
        /// <returns></returns>
        public static SiloAddress ToSiloAddress(this Uri uri)
        {
            switch (uri.Scheme)
            {
                case "gwy.tcp":
                    return SiloAddress.New(uri.ToIPEndPoint(), uri.Segments.Length > 1 ? int.Parse(uri.Segments[1]) : 0);
            }
            return null;
        }

        /// <summary>
        /// Represent an IP end point in the gateway URI format..
        /// </summary>
        /// <param name="ep">The input IP end point</param>
        /// <returns></returns>
        public static Uri ToGatewayUri(this System.Net.IPEndPoint ep)
        {
            return new Uri(string.Format("gwy.tcp://{0}:{1}/0", ep.Address, ep.Port));
        }

        /// <summary>
        /// Represent a silo address in the gateway URI format.
        /// </summary>
        /// <param name="address">The input silo address</param>
        /// <returns></returns>
        public static Uri ToGatewayUri(this SiloAddress address)
        {
            return new Uri(string.Format("gwy.tcp://{0}:{1}/{2}", address.Endpoint.Address, address.Endpoint.Port, address.Generation));
        }

        /// <summary>
        /// Represent a silo instance entry in the gateway URI format.
        /// </summary>
        /// <param name="address">The input silo instance</param>
        /// <returns></returns>
        internal static Uri ToGatewayUri(this AzureUtils.SiloInstanceTableEntry gateway)
        {
            int proxyPort = 0;
            if (!string.IsNullOrEmpty(gateway.ProxyPort))
                int.TryParse(gateway.ProxyPort, out proxyPort);

            return new Uri(string.Format("gwy.tcp://{0}:{1}/{2}", gateway.Address, proxyPort, gateway.Generation));
        }

        /// <summary>
        /// Calculates an integer hash value based on the consistent identity hash of a string.
        /// </summary>
        /// <param name="text">The string to hash.</param>
        /// <returns>An integer hash for the string.</returns>
        public static int CalculateIdHash(string text)
        {
            SHA256 sha = new SHA256CryptoServiceProvider(); // This is one implementation of the abstract class SHA1.
            int hash = 0;
            try
            {
                byte[] data = Encoding.Unicode.GetBytes(text);
                byte[] result = sha.ComputeHash(data);
                for (int i = 0; i < result.Length; i += 4)
                {
                    int tmp = (result[i] << 24) | (result[i + 1] << 16) | (result[i + 2] << 8) | (result[i + 3]);
                    hash = hash ^ tmp;
                }
            }
            finally
            {
                sha.Dispose();
            }
            return hash;
        }

        public static bool TryFindException(Exception original, Type targetType, out Exception target)
        {
            if (original.GetType() == targetType)
            {
                target = original;
                return true;
            }
            else if (original is AggregateException)
            {
                var baseEx = original.GetBaseException();
                if (baseEx.GetType() == targetType)
                {
                    target = baseEx;
                    return true;
                }
                else
                {
                    var newEx = ((AggregateException)original).Flatten();
                    foreach (var exc in newEx.InnerExceptions)
                    {
                        if (exc.GetType() == targetType)
                        {
                            target = newEx;
                            return true;
                        }
                    }
                }
            }
            target = null;
            return false;
        }

        public static void SafeExecute(Action action, TraceLogger logger = null, string caller = null)
        {
            SafeExecute(action, logger, caller==null ? (Func<string>)null : () => caller);
        }

        // a function to safely execute an action without any exception being thrown.
        // callerGetter function is called only in faulty case (now string is generated in the success case).
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public static void SafeExecute(Action action, TraceLogger logger, Func<string> callerGetter)
        {
            try
            {
                action();
            }
            catch (Exception exc)
            {
                try
                {
                    if (logger != null)
                    {
                        string caller = null;
                        if (callerGetter != null)
                        {
                            caller = callerGetter();
                        }
                        foreach (var e in exc.FlattenAggregate())
                        {
                            logger.Warn(ErrorCode.Runtime_Error_100325, String.Format("Ignoring {0} exception thrown from an action called by {1}.", e.GetType().FullName, caller ?? String.Empty), exc);
                        }
                    }
                }
                catch (Exception)
                {
                    // now really, really ignore.
                }
            }
        }

        /// <summary>
        /// Get the last characters of a string
        /// </summary>
        /// <param name="s"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public static string Tail(this string s, int count)
        {
            return s.Substring(Math.Max(0, s.Length - count));
        }

        public static TimeSpan Since(DateTime start)
        {
            return DateTime.UtcNow.Subtract(start);
        }

        public static List<T> ObjectToList<T>(object data)
        {
            if (data is List<T>) return (List<T>) data;

            T[] dataArray;
            if (data is ArrayList)
            {
                dataArray = (T[]) (data as ArrayList).ToArray(typeof(T));
            }
            else if (data is ICollection<T>)
            {
                dataArray = (data as ICollection<T>).ToArray();
            }
            else
            {
                throw new InvalidCastException(string.Format(
                    "Connet convert type {0} to type List<{1}>", 
                    TypeUtils.GetFullName(data.GetType()),
                    TypeUtils.GetFullName(typeof(T))));
            }
            var list = new List<T>();
            list.AddRange(dataArray);
            return list;
        }

        public static List<Exception> FlattenAggregate(this Exception exc)
        {
            var result = new List<Exception>();
            if (exc is AggregateException)
                result.AddRange(exc.InnerException.FlattenAggregate());
            else
                result.Add(exc);
            return result;
        }

        public static AggregateException Flatten(this ReflectionTypeLoadException rtle)
        {
            // if ReflectionTypeLoadException is thrown, we need to provide the
            // LoaderExceptions property in order to make it meaningful.
            var all = new List<Exception> { rtle };
            all.AddRange(rtle.LoaderExceptions);
            throw new AggregateException("A ReflectionTypeLoadException has been thrown. The original exception and the contents of the LoaderExceptions property have been aggregated for your convenence.", all);
        }
    }
}
