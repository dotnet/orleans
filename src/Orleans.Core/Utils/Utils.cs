using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    /// <summary>
    /// The Utils class contains a variety of utility methods for use in application and grain code.
    /// </summary>
    public static class Utils
    {
        /// <summary>
        /// Returns a human-readable text string that describes an IEnumerable collection of objects.
        /// </summary>
        /// <typeparam name="T">The type of the list elements.</typeparam>
        /// <param name="collection">The IEnumerable to describe.</param>
        /// <param name="toString">Converts the element to a string. If none specified, <see cref="object.ToString"/> will be used.</param>
        /// <param name="separator">The separator to use.</param>
        /// <param name="putInBrackets">Puts elements within brackets</param>
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
        /// <param name="dict">The dictionary to describe.</param>
        /// <param name="toString">Converts the element to a string. If none specified, <see cref="object.ToString"/> will be used.</param>
        /// <param name="separator">The separator to use. If none specified, the elements should appear separated by a new line.</param>
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

        public static long TicksToMilliSeconds(long ticks) => ticks / TimeSpan.TicksPerMillisecond;

        public static float AverageTicksToMilliSeconds(float ticks) => ticks / TimeSpan.TicksPerMillisecond;

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
        /// Parse a Uri as a Silo address, excluding the generation identifier.
        /// </summary>
        /// <param name="uri">The input Uri</param>
        public static SiloAddress ToGatewayAddress(this Uri uri)
        {
            switch (uri.Scheme)
            {
                case "gwy.tcp":
                    return SiloAddress.New(uri.ToIPEndPoint(), 0);
            }

            return null;
        }

        /// <summary>
        /// Represent an IP end point in the gateway URI format..
        /// </summary>
        /// <param name="ep">The input IP end point</param>
        /// <returns></returns>
        public static Uri ToGatewayUri(this System.Net.IPEndPoint ep) => new Uri("gwy.tcp://" + ep.ToString() + "/0");

        /// <summary>
        /// Represent a silo address in the gateway URI format.
        /// </summary>
        /// <param name="address">The input silo address</param>
        /// <returns></returns>
        public static Uri ToGatewayUri(this SiloAddress address) => new Uri("gwy.tcp://" + address.Endpoint.ToString() + "/" + address.Generation.ToString());

        /// <summary>
        /// Calculates an integer hash value based on the consistent identity hash of a string.
        /// </summary>
        /// <param name="text">The string to hash.</param>
        /// <returns>An integer hash for the string.</returns>
        public static int CalculateIdHash(string text)
        {
            var input = BitConverter.IsLittleEndian ? MemoryMarshal.AsBytes(text.AsSpan()) : Encoding.Unicode.GetBytes(text);

            Span<int> result = stackalloc int[256 / 8 / sizeof(int)];
            var sha = SHA256.Create();
            sha.TryComputeHash(input, MemoryMarshal.AsBytes(result), out _);
            sha.Dispose();

            var hash = 0;
            for (var i = 0; i < result.Length; i++) hash ^= result[i];
            return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(hash) : hash;
        }

        /// <summary>
        /// Calculates a Guid hash value based on the consistent identity a string.
        /// </summary>
        /// <param name="text">The string to hash.</param>
        /// <returns>An integer hash for the string.</returns>
        internal static Guid CalculateGuidHash(string text)
        {
            var input = BitConverter.IsLittleEndian ? MemoryMarshal.AsBytes(text.AsSpan()) : Encoding.Unicode.GetBytes(text);

            Span<byte> result = stackalloc byte[256 / 8];
            var sha = SHA256.Create();
            sha.TryComputeHash(input, result, out _);
            sha.Dispose();

            MemoryMarshal.AsRef<long>(result) ^= MemoryMarshal.Read<long>(result.Slice(16));
            MemoryMarshal.AsRef<long>(result.Slice(8)) ^= MemoryMarshal.Read<long>(result.Slice(24));
            return BitConverter.IsLittleEndian ? MemoryMarshal.Read<Guid>(result) : new Guid(result.Slice(0, 16));
        }

        public static void SafeExecute(Action action, ILogger logger = null, string caller = null)
        {
            SafeExecute(action, logger, (object)caller);
        }

        // a function to safely execute an action without any exception being thrown.
        // callerGetter function is called only in faulty case (now string is generated in the success case).
        public static void SafeExecute(Action action, ILogger logger, Func<string> callerGetter) => SafeExecute(action, logger, (object)callerGetter);

        private static void SafeExecute(Action action, ILogger logger, object callerGetter)
        {
            try
            {
                action();
            }
            catch (Exception exc)
            {
                if (logger != null)
                {
                    try
                    {
                        string caller = null;
                        switch (callerGetter)
                        {
                            case string value:
                                caller = value;
                                break;
                            case Func<string> func:
                                try
                                {
                                    caller = func();
                                }
                                catch
                                {
                                }

                                break;
                        }

                        foreach (var e in exc.FlattenAggregate())
                        {
                            logger.Warn(ErrorCode.Runtime_Error_100325,
                                $"Ignoring {e.GetType().FullName} exception thrown from an action called by {caller ?? String.Empty}.", exc);
                        }
                    }
                    catch
                    {
                        // now really, really ignore.
                    }
                }
            }
        }

        public static TimeSpan Since(DateTime start)
        {
            return DateTime.UtcNow.Subtract(start);
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

        /// <summary>
        /// </summary>
        public static IEnumerable<List<T>> BatchIEnumerable<T>(this IEnumerable<T> sequence, int batchSize)
        {
            var batch = new List<T>(batchSize);
            foreach (var item in sequence)
            {
                batch.Add(item);
                // when we've accumulated enough in the batch, send it out  
                if (batch.Count >= batchSize)
                {
                    yield return batch; // batch.ToArray();
                    batch = new List<T>(batchSize);
                }
            }
            if (batch.Count > 0)
            {
                yield return batch; //batch.ToArray();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string GetStackTrace(int skipFrames = 0)
        {
            skipFrames += 1; //skip this method from the stack trace
            return new System.Diagnostics.StackTrace(skipFrames).ToString();
        }
    }
}
