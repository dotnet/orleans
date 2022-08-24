using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

#nullable enable
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
        public static string EnumerableToString<T>(IEnumerable<T>? collection, Func<T, string>? toString = null,
                                                        string separator = ", ", bool putInBrackets = true)
        {
            if (collection == null)
                return putInBrackets ? "[]" : "null";

            if (collection is ICollection<T> { Count: 0 })
                return putInBrackets ? "[]" : "";

            var enumerator = collection.GetEnumerator();
            if (!enumerator.MoveNext())
                return putInBrackets ? "[]" : "";

            var firstValue = enumerator.Current;
            if (!enumerator.MoveNext())
            {
                return putInBrackets
                    ? toString != null ? $"[{toString(firstValue)}]" : firstValue == null ? "[null]" : $"[{firstValue}]"
                    : toString != null ? toString(firstValue) : firstValue == null ? "null" : (firstValue.ToString() ?? "");
            }

            var sb = new StringBuilder();
            if (putInBrackets) sb.Append('[');

            if (toString != null) sb.Append(toString(firstValue));
            else if (firstValue == null) sb.Append("null");
            else sb.Append($"{firstValue}");

            do
            {
                sb.Append(separator);

                var value = enumerator.Current;
                if (toString != null) sb.Append(toString(value));
                else if (value == null) sb.Append("null");
                else sb.Append($"{value}");
            } while (enumerator.MoveNext());

            if (putInBrackets) sb.Append(']');
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
        public static string DictionaryToString<T1, T2>(ICollection<KeyValuePair<T1, T2>> dict, Func<T2, string?>? toString = null, string? separator = null)
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

                string? val;
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
            return $"{timeSpan.Hours}h:{timeSpan.Minutes}m:{timeSpan.Seconds}s.{timeSpan.Milliseconds}ms";
        }

        public static long TicksToMilliSeconds(long ticks) => ticks / TimeSpan.TicksPerMillisecond;

        public static float AverageTicksToMilliSeconds(float ticks) => ticks / TimeSpan.TicksPerMillisecond;

        /// <summary>
        /// Parse a Uri as an IPEndpoint.
        /// </summary>
        /// <param name="uri">The input Uri</param>
        /// <returns></returns>
        public static System.Net.IPEndPoint? ToIPEndPoint(this Uri uri) => uri.Scheme switch
        {
            "gwy.tcp" => new System.Net.IPEndPoint(System.Net.IPAddress.Parse(uri.Host), uri.Port),
            _ => null,
        };

        /// <summary>
        /// Parse a Uri as a Silo address, excluding the generation identifier.
        /// </summary>
        /// <param name="uri">The input Uri</param>
        public static SiloAddress? ToGatewayAddress(this Uri uri) => uri.Scheme switch
        {
            "gwy.tcp" => SiloAddress.New(System.Net.IPAddress.Parse(uri.Host), uri.Port, 0),
            _ => null,
        };

        /// <summary>
        /// Represent an IP end point in the gateway URI format..
        /// </summary>
        /// <param name="ep">The input IP end point</param>
        /// <returns></returns>
        public static Uri ToGatewayUri(this System.Net.IPEndPoint ep) => new($"gwy.tcp://{new SpanFormattableIPEndPoint(ep)}/0");

        /// <summary>
        /// Represent a silo address in the gateway URI format.
        /// </summary>
        /// <param name="address">The input silo address</param>
        /// <returns></returns>
        public static Uri ToGatewayUri(this SiloAddress address) => new($"gwy.tcp://{new SpanFormattableIPEndPoint(address.Endpoint)}/{address.Generation}");

        public static void SafeExecute(Action action)
        {
            try
            {
                action();
            }
            catch { }
        }

        public static void SafeExecute(Action action, ILogger? logger = null, string? caller = null)
        {
            try
            {
                action();
            }
            catch (Exception exc)
            {
                if (logger != null)
                    LogIgnoredException(logger, exc, caller);
            }
        }

        internal static void LogIgnoredException(ILogger logger, Exception exc, string? caller)
        {
            try
            {
                if (exc is AggregateException { InnerExceptions.Count: 1 })
                    exc = exc.InnerException!;

                logger.LogWarning(
                    (int)ErrorCode.Runtime_Error_100325,
                    exc,
                    "Ignoring {ExceptionType} exception thrown from an action called by {Caller}.",
                    exc.GetType().FullName,
                    caller ?? string.Empty);
            }
            catch
            {
                // now really, really ignore.
            }
        }

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
