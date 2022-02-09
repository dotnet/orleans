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
            return exception == null ? String.Empty : PrintException_Helper(exception, 0, true);
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

        private static string PrintException_Helper(Exception exception, int level, bool includeStackTrace)
        {
            if (exception == null) return String.Empty;
            var sb = new StringBuilder();
            sb.Append(PrintOneException(exception, level, includeStackTrace));
            if (exception is ReflectionTypeLoadException)
            {
                Exception[] loaderExceptions =
                    ((ReflectionTypeLoadException)exception).LoaderExceptions;
                if (loaderExceptions == null || loaderExceptions.Length == 0)
                {
                    sb.Append("No LoaderExceptions found");
                }
                else
                {
                    foreach (Exception inner in loaderExceptions)
                    {
                        // call recursively on all loader exceptions. Same level for all.
                        sb.Append(PrintException_Helper(inner, level + 1, includeStackTrace));
                    }
                }
            }
            else if (exception is AggregateException)
            {
                var innerExceptions = ((AggregateException)exception).InnerExceptions;
                if (innerExceptions == null) return sb.ToString();

                foreach (Exception inner in innerExceptions)
                {
                    // call recursively on all inner exceptions. Same level for all.
                    sb.Append(PrintException_Helper(inner, level + 1, includeStackTrace));
                }
            }
            else if (exception.InnerException != null)
            {
                // call recursively on a single inner exception.
                sb.Append(PrintException_Helper(exception.InnerException, level + 1, includeStackTrace));
            }
            return sb.ToString();
        }

        private static string PrintOneException(Exception exception, int level, bool includeStackTrace)
        {
            if (exception == null) return String.Empty;
            string stack = String.Empty;
            if (includeStackTrace && exception.StackTrace != null)
                stack = String.Format(Environment.NewLine + exception.StackTrace);

            string message = exception.Message;
            var excType = exception.GetType();

            Func<Exception, string> decoder;
            if (exceptionDecoders.TryGetValue(excType, out decoder))
                message = decoder(exception);

            return String.Format(Environment.NewLine + "Exc level {0}: {1}: {2}{3}",
                level,
                exception.GetType(),
                message,
                stack);
        }
    }
}
