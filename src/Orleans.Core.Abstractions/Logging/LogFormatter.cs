using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// Formats values for logging purposes.
    /// </summary>
    public static class LogFormatter
    {
        public const int MAX_LOG_MESSAGE_SIZE = 20000;
        private const string TIME_FORMAT = "HH:mm:ss.fff 'GMT'"; // Example: 09:50:43.341 GMT
        private const string DATE_FORMAT = "yyyy-MM-dd " + TIME_FORMAT; // Example: 2010-09-02 09:50:43.341 GMT - Variant of UniversalSorta­bleDateTimePat­tern
        private static readonly ConcurrentDictionary<Type, Func<Exception, string>> exceptionDecoders = new ConcurrentDictionary<Type, Func<Exception, string>>();

        /// <summary>
        /// Utility function to convert a <c>DateTime</c> object into printable data format used by the Logger subsystem.
        /// </summary>
        /// <param name="date">The <c>DateTime</c> value to be printed.</param>
        /// <returns>Formatted string representation of the input data, in the printable format used by the Logger subsystem.</returns>
        public static string PrintDate(DateTime date)
        {
            return date.ToString(DATE_FORMAT, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses a date.
        /// </summary>
        /// <param name="dateStr">The date string.</param>
        /// <returns>The parsed date.</returns>
        public static DateTime ParseDate(string dateStr)
        {
            return DateTime.ParseExact(dateStr, DATE_FORMAT, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Utility function to convert a <c>DateTime</c> object into printable time format used by the Logger subsystem.
        /// </summary>
        /// <param name="date">The <c>DateTime</c> value to be printed.</param>
        /// <returns>Formatted string representation of the input data, in the printable format used by the Logger subsystem.</returns>
        public static string PrintTime(DateTime date)
        {
            return date.ToString(TIME_FORMAT, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Utility function to convert an exception into printable format, including expanding and formatting any nested sub-expressions.
        /// </summary>
        /// <param name="exception">The exception to be printed.</param>
        /// <returns>Formatted string representation of the exception, including expanding and formatting any nested sub-expressions.</returns>
        public static string PrintException(Exception exception)
        {
            if (exception == null)
                return "";

            var sb = new StringBuilder();
            PrintException_Helper(sb, exception, 0);
            return sb.ToString();
        }

        /// <summary>
        /// Configures the exception decoder for the specified exception type.
        /// </summary>
        /// <param name="exceptionType">The exception type to configure a decoder for.</param>
        /// <param name="decoder">The decoder.</param>
        public static void SetExceptionDecoder(Type exceptionType, Func<Exception, string> decoder)
        {
            exceptionDecoders.TryAdd(exceptionType, decoder);
        }

        private static void PrintException_Helper(StringBuilder sb, Exception exception, int level)
        {
            if (exception == null) return;

            var message = exceptionDecoders.TryGetValue(exception.GetType(), out var decoder) ? decoder(exception) : exception.Message;
            sb.Append($"{Environment.NewLine}Exc level {level}: {exception.GetType()}: {message}");

            if (exception.StackTrace is { } stack)
            {
                sb.Append($"{Environment.NewLine}{stack}");
            }

            if (exception is ReflectionTypeLoadException typeLoadException)
            {
                var loaderExceptions = typeLoadException.LoaderExceptions;
                if (loaderExceptions == null || loaderExceptions.Length == 0)
                {
                    sb.Append("No LoaderExceptions found");
                }
                else
                {
                    foreach (Exception inner in loaderExceptions)
                    {
                        // call recursively on all loader exceptions. Same level for all.
                        PrintException_Helper(sb, inner, level + 1);
                    }
                }
            }
            else if (exception.InnerException != null)
            {
                if (exception is AggregateException { InnerExceptions: { Count: > 1 } innerExceptions })
                {
                    foreach (Exception inner in innerExceptions)
                    {
                        // call recursively on all inner exceptions. Same level for all.
                        PrintException_Helper(sb, inner, level + 1);
                    }
                }
                else
                {
                    // call recursively on a single inner exception.
                    PrintException_Helper(sb, exception.InnerException, level + 1);
                }
            }
        }
    }
}
